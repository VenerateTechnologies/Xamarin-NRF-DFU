using System;
using System.Collections.Generic;
using System.Text;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;

namespace Plugin.XamarinNordicDFU
{
    public class DFUScanner
    {
        public Func<IDevice, bool> Success { get; set; } = null;

        public async void Scan()
        {
            var adapter = CrossBluetoothLE.Current.Adapter;
            
            adapter.DeviceDiscovered += async (sender, result) =>
            {
                if (Success != null)
                {
                    var bStatus = Success.Invoke(result.Device);

                    if (bStatus)
                    {
                        await adapter.StopScanningForDevicesAsync();
                    }
                }
                else
                {
                    await adapter.StopScanningForDevicesAsync();
                }
            };

            await adapter.StartScanningForDevicesAsync(new Guid[]
            {
                DFU.DfuServicePublic
            });
        }
    }
}
