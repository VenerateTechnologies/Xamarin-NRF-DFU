// Copyright (c) 2018 SAF Tehnika. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Threading.Tasks;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using System.Diagnostics;
using System.Reactive.Linq;
using System.IO;
using System.Collections.Generic;

namespace Plugin.XamarinNordicDFU
{
    partial class DFU
    {
        #region Helper Functions
        /// <summary>
        /// Assert that commmand executed successfully and raise error if not
        /// </summary>
        /// <param name="result"></param>
        private void AssertSuccess(byte[] result)
        {
            /*
			* The response received from the DFU device contains:
			* +---------+--------+----------------------------------------------------+
			* | byte no | value  | description                                        |
			* +---------+--------+----------------------------------------------------+
			* | 0       | 0x60   | Response code                                      |
			* | 1       | 0x06   | The Op Code of a request that this response is for |
			* | 2       | STATUS | Status code                                        |
            * | ...     | ...    | ...                                                |
			* +---------+--------+----------------------------------------------------+
			*/
            if (result[2] != SecureDFUCommandSuccess)
            {
                InvokeError(result);
                throw new Exception(String.Format("Assertion failed while testing command [{0}] response status: [{1}]", result[1], result[2]));
            }
        }
        /// <summary>
        /// Exit DFU mode
        /// </summary>
        /// <param name="controlPoint"></param>
        /// <returns></returns>
        private async Task ExitSecureDFU(ICharacteristic controlPoint)
        {
            var notif = GetTimedNotification(controlPoint);
            await controlPoint.WriteAsync(new byte[] { });
            var result = await notif.Task;

            AssertSuccess(result);
        }
        /// <summary>
        /// Invoke error from BLE response
        /// </summary>
        /// <param name="response"></param>
        private void InvokeError(byte[] result)
        {
            var err = (ResponseErrors)result[2];
            if (err == ResponseErrors.NRF_DFU_RES_CODE_EXT_ERROR)
            {
                var error = (ExtendedErrors)result[3];
                DFUEvents.OnExtendedError?.Invoke(error);
            }
            else
            {
                var error = (ResponseErrors)result[3];
                DFUEvents.OnResponseError?.Invoke(error);
            }
        }
        /// <summary>
        /// Sets the UINT16 value in data converted to LSB at data offset
        /// </summary>
        /// <param name="data"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        private void SetUint16LSB(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
        }
        /// <summary>
        /// Sets the UINT32 value in data converted to LSB at data offset
        /// </summary>
        /// <param name="data"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        private void SetUInt32LSB(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
            data[offset + 2] = (byte)((value >> 16) & 0xFF);
            data[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
        /// <summary>
        /// Set Packet Receipt Notification (PRN) value.
        /// Sets the number of packets to be sent before receiving a Packet Receipt Notification. The default is 0.
        /// </summary>
        /// <param name="device"></param>
        /// <param name="prn"></param>
        /// <returns></returns>
        private async Task SetPRN(ICharacteristic controlPoint, int prn)
        {
            // PRN value should be LSB
            byte[] data = new byte[] {
                CSetPRN,
                0,
                0
            };
            SetUint16LSB(data, 1, prn);

            var notif = GetTimedNotification(controlPoint);
            await controlPoint.WriteAsync(data);

            var result = await notif.Task;
            
            Debug.WriteLineIf(LogLevelDebug, String.Format("Received PRN set response: {0}", BitConverter.ToString(result)));

            AssertSuccess(result);
        }
        /// <summary>
        /// Creates an object with the given type and selects it. Removes an old object of the same type (if such an object exists).
        /// </summary>
        /// <param name="device"></param>
        /// <param name="type"></param>
        /// <param name="size"></param>
        /// <returns></returns>
        private async Task CreateCommand(ICharacteristic controlPoint, SecureDFUCreateCommandType type, int size)
        {
            byte[] data = new byte[]{
                CCreateObject,
                (byte)type,
                0,
                0,
                0,
                0
            };
            SetUInt32LSB(data, 2, size);
            
            var notif = GetTimedNotification(controlPoint);
            await controlPoint.WriteAsync(data);
            var result = await notif.Task;

            AssertSuccess(result);

            Debug.WriteLineIf(LogLevelDebug, String.Format("Command object created {0}", BitConverter.ToString(result)));
        }
        /// <summary>
        /// Select command before starting to upload something
        /// </summary>
        /// <param name="device"></param>
        /// <param name="command"></param>
        /// <returns></returns>
        private async Task<ObjectInfo> SelectCommand(ICharacteristic controlPoint, SecureDFUSelectCommandType command)
        {
            ObjectInfo info = new ObjectInfo();
            var tcs = new TaskCompletionSource<bool>();
            
            var notif = GetTimedNotification(controlPoint);
            
            await controlPoint.WriteAsync(new byte[] { SelectCommandPrefix, (byte)command });

            var result = await notif.Task;
            byte[] response = result;

            AssertSuccess(result);

            info.maxSize = (int)BitConverter.ToUInt32(response, 3);
            info.offset = (int)BitConverter.ToUInt32(response, 3 + 4);
            info.CRC32 = (int)BitConverter.ToUInt32(response, 3 + 8);

            return info;
        }
        /// <summary>
        /// Sets checksum from (Response PRN) or (Response Calculate CRC) responses
        /// </summary>
        /// <param name="checksum"></param>
        /// <param name="data"></param>
        private void SetChecksum(ObjectChecksum checksum, byte[] data)
        {
            checksum.offset = (int)BitConverter.ToUInt32(data, 3);
            checksum.CRC32 = (int)BitConverter.ToUInt32(data, 3 + 4);
        }
        /// <summary>
        /// Request and read DFU checksum from control point after data have been sent
        /// </summary>
        /// <param name="device"></param>
        /// <returns></returns>
        private async Task<ObjectChecksum> ReadChecksum(ICharacteristic controlPoint)
        {
            Debug.WriteLineIf(LogLevelDebug, String.Format("Begin read checksum"));
            ObjectChecksum checksum = new ObjectChecksum();

            byte[] result = null;

            for (var retry = 0; retry < MaxRetries; retry++)
            {
                try
                {
                    // Request checksum
                    var notif = GetTimedNotification(controlPoint);

                    var re = await controlPoint.WriteAsync(CCalculateCRC);
                    Debug.WriteLine(re);
                    result = (await notif.Task);
                    break;
                }
                catch (Exception)
                {
                    await Task.Delay(100);
                }
                finally
                {
                    if(retry == MaxRetries)
                    {
                        throw new Exception("DFU Timeout");
                    }
                }
            }

            Debug.WriteLineIf(LogLevelDebug, String.Format("Check Sum Response {0}", BitConverter.ToString(result)));

            AssertSuccess(result);

            SetChecksum(checksum, result);

            Debug.WriteLineIf(LogLevelDebug, String.Format("End read checksum"));
            return checksum;
        }
        /// <summary>
        /// Write data to data point characteristic
        /// </summary>
        /// <param name="crc32"></param>
        /// <param name="file"></param>
        /// <param name="device"></param>
        /// <param name="MTU"></param>
        /// <param name="transferBytes"></param>
        /// <param name="remoteOffset"></param>
        /// <returns></returns>
        private async Task<int> TransferData(ICharacteristic packetPoint, CRC32 crc, Stream file, /*int prn = -1,*/ int offsetStart = 0, int offsetEnd = -1, int MTU = 20)
        {
            const int RESERVED_BYTES = 3;
            int bytesWritten = 0;
            try
            {
                int MAX_READ_BYTES = MTU - RESERVED_BYTES;
                Debug.WriteLineIf(LogLevelDebug, String.Format("Start of data transfer flen: {0}", file.Length));
                file.Seek(offsetStart, SeekOrigin.Begin);
                
                byte[] data = new byte[MAX_READ_BYTES];
                
                for (int packetsSent = 0, offset = offsetStart; ; )
                {
                    int bytesToRead = MAX_READ_BYTES;
                    if(offsetEnd > -1)
                    {
                        int endOffsetDiff = offsetEnd - offset;
                        if(endOffsetDiff <= 0)
                        {
                            break;
                        }
                        bytesToRead = Math.Min(bytesToRead, endOffsetDiff);
                    }
                    Debug.WriteLineIf(LogLevelDebug, String.Format("Reading {0} bytes from file stream", bytesToRead));
                    int size = file.Read(data, 0, bytesToRead);
                    if (size <= 0)
                    {
                        break;
                    }
                    offset += size;
                    byte[] pending;

                    if (size < MAX_READ_BYTES)
                    {
                        pending = new byte[size];
                        Array.Copy(data, pending, size);
                    }
                    else
                    {
                        pending = data;
                    }

                    // Specific for iOS, if no timeout, then sending happens to be blocked
                    await Task.Delay(1);

                    await packetPoint.WriteAsync(pending);

                    packetsSent++;
                    bytesWritten += pending.Length;

                    crc.Update(pending);
                    Debug.WriteLineIf(LogLevelDebug, String.Format("### CRC32 Step value, crc: {0} offset:{2} size: {1} packetLocalNo:{3}", crc.Value, pending.Length, offset, packetsSent));
                }
                Debug.WriteLineIf(LogLevelDebug, String.Format("End of data transfer"));
            }
            catch (Exception ex)
            {
                Debug.WriteLineIf(LogLevelDebug, String.Format("Error while transfering data"));
                Debug.Write(ex);
            }
            return bytesWritten;
        }
        /// <summary>
        /// Run "Execute" command when all data is transfered
        /// </summary>
        /// <param name="device"></param>
        /// <returns></returns>
        private async Task ExecuteCommand(ICharacteristic controlPoint, bool skipRegistring = false)
        {
            var notif = GetTimedNotification(controlPoint);
            await controlPoint.WriteAsync(CExecute);

            var result = (await notif.Task);

            Debug.WriteLineIf(LogLevelDebug, String.Format("Execute command result {0}", BitConverter.ToString(result)));
            AssertSuccess(result);
        }
        /// <summary>
        /// Gets current object end offset
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="objectSize"></param>
        /// <param name="firmwareSize"></param>
        /// <returns></returns>
        private int GetCurrentObjectEnd(int offset, int objectSize, long firmwareSize)
        {
            int offsetEnd;
            int objectNo = offset / objectSize;

            offsetEnd = objectSize * objectNo + objectSize;
            if (offsetEnd < firmwareSize)
            {
                return offsetEnd;
            }
            return (int)firmwareSize;
        }
        /// <summary>
        /// Get async notification with timeout
        /// </summary>
        /// <param name="characteristic"></param>
        /// <returns></returns>
        private TaskCompletionSource<byte[]> GetTimedNotification(ICharacteristic characteristic)
        {
            var tcs = new TaskCompletionSource<byte[]>();

            bool notificationReceived = false;
            IDisposable disposable = null;
            object locked = new object();
            Task task = Task.Delay(OperationTimeout).ContinueWith(t => {
                lock (locked)
                {
                    if (!notificationReceived)
                    {
                        disposable?.Dispose();
                        tcs.TrySetException(new Exception("DFU Timeout"));
                    }
                }
            });

            characteristic.ValueUpdated += (_sender, result) =>
            {
                lock (locked)
                {
                    notificationReceived = true;
                }

                disposable?.Dispose();
                tcs.TrySetResult(result.Characteristic.Value);
            };

            return tcs;
        }
        #endregion
    }
}
