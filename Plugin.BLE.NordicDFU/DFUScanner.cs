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

        public async void Scan(string Name = @"", string Guid = @"")
        {
            var adapter = CrossBluetoothLE.Current.Adapter;
            
            adapter.DeviceDiscovered += async (sender, result) =>
            {
                if (Success != null)
                {
                    var strGuid = result.Device.Id.ToString().ToLower().Replace("-", "");

                    if (result.Device != null && !string.IsNullOrEmpty(result.Device.Name) &&
                        result.Device.Name.Equals(Name) && !string.IsNullOrEmpty(Guid))
                    {
                        var bStatus = Success.Invoke(result.Device);

                        if (bStatus)
                        {
                            await adapter.StopScanningForDevicesAsync();
                        }
                    }
                    else if (result.Device != null && !string.IsNullOrEmpty(strGuid) &&
                             strGuid.EndsWith(Guid.ToLower().Replace("-", "")) && !string.IsNullOrEmpty(Guid))
                    {
                        var bStatus = Success.Invoke(result.Device);

                        if (bStatus)
                        {
                            await adapter.StopScanningForDevicesAsync();
                        }
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
