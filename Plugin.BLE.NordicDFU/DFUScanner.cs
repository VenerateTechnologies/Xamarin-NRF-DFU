using System;
using System.Collections.Generic;
using System.Text;
using Plugin.BluetoothLE;
using System.Reactive.Linq;

namespace Plugin.XamarinNordicDFU
{
    public class DFUScanner
    {
        public Func<IDevice, bool> Success { get; set; } = null;

        public async void Scan(string Name = @"", string Guid = @"")
        {
            var adapter = CrossBleAdapter.Current;

            adapter.Scan(new ScanConfig()
            {
                ScanType = BleScanType.Balanced,
                ServiceUuids = new List<Guid>(new Guid[]
                {
                    DFU.DfuServicePublic
                })
            }).Subscribe(result =>
            {
                if (Success != null)
                {
                    var strGuid = result.Device.Uuid.ToString().ToLower().Replace("-", "");

                    if (result.Device != null && !string.IsNullOrEmpty(result.Device.Name) &&
                        result.Device.Name.Equals(Name) && !string.IsNullOrEmpty(Name))
                    {
                        var bStatus = Success.Invoke(result.Device);

                        if (bStatus)
                        {
                            adapter.StopScan();
                        }
                    }
                    else if (result.Device != null && !string.IsNullOrEmpty(strGuid) &&
                             strGuid.EndsWith(Guid.ToLower().Replace("-", "")) && !string.IsNullOrEmpty(Guid))
                    {
                        var bStatus = Success.Invoke(result.Device);

                        if (bStatus)
                        {
                            adapter.StopScan();
                        }
                    }
                }
                else
                {
                }
            });
        }
    }
}
