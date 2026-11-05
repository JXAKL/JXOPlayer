using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;

namespace JXOPlayer.Compatibility
{
    // Manual connections use the user's selected transport. Automatic discovery
    // keeps the original device handshake and preferred Bluetooth restrictions.
    public static class ConnectionCompatibility
    {
        const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        static readonly NLog.Logger Log=NLog.LogManager.GetLogger("JXOPlayer.DeviceConnection");
        static string BluetoothScanError;
        public static async Task<T> ScanBridge<T>(object client,bool cachedOnly,string transportType,CancellationToken token) where T:new()
        {
            try{
                var command=new{action="scan",cachedOnly,transportType};
                var task=(Task)client.GetType().GetMethod("SendCommandAsync",Flags).Invoke(client,new object[]{command,cachedOnly?10000:15000,token});
                await task.ConfigureAwait(false);
                var response=Get(task,"Result");
                if(Get(response,"Ok") is true){BluetoothScanError=null;return Get(response,"Devices") is T devices?devices:new T();}
                BluetoothScanError=Get(response,"Error") as string??"蓝牙扫描失败，请检查 Windows 蓝牙开关及驱动。";
                Log.Warn("Bluetooth scan failed: {0}",BluetoothScanError);return new T();
            }catch(Exception ex){BluetoothScanError="蓝牙扫描失败："+(ex.InnerException??ex).Message;throw;}
        }
        static readonly Lazy<string> BluetoothDependencyError=new Lazy<string>(()=>{
            try{
                return WindowsBluetoothBackend.GetAvailabilityError();
            }catch(Exception ex){
                Log.Error(ex,"Windows Bluetooth backend cannot load");
                return "Windows 蓝牙组件无法加载，请重新完整解压或安装软件。";
            }
        });
        public static string BluetoothHint(object dialog)
        {
            var target=Get(dialog,"BluetoothTarget");
            if(target==null)return "蓝牙模式不可用";
            if(Get(target,"IsRefreshBusy") is bool busy && busy)return "正在扫描蓝牙设备";
            return BluetoothDependencyError.Value??BluetoothScanError??"请选择自己的设备。经典蓝牙需提供 SPP；BLE 支持 FFE0/FFE1 或 Nordic UART 串口服务。请确认 Windows 蓝牙已开启，固件提供对应服务。";
        }
        static object Get(object value,string name)=>value?.GetType().GetProperty(name,Flags)?.GetValue(value);
        static void Set(object value,string name,object data)=>value.GetType().GetProperty(name,Flags).SetValue(value,data);
        static async ValueTask<bool> Legacy(object target,int mode)
        {
            var method=target.GetType().GetMethod("OnConnectingLegacyAsync",Flags);
            try {return await ((ValueTask<bool>)method.Invoke(target,new[]{Enum.ToObject(method.GetParameters()[0].ParameterType,mode)})).ConfigureAwait(false);}
            catch(TargetInvocationException ex) when(ex.InnerException!=null){ExceptionDispatchInfo.Capture(ex.InnerException).Throw();throw;}
        }
        public static bool SerialVisible(object port)=>port!=null && Regex.IsMatch((string)Get(port,"PortName")??"",@"^COM[0-9]+$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        public static bool KnownSerial(object port)
        {
            if(!SerialVisible(port))return false;
            var description=string.Join(" ",new[]{"Name","Description","Manufacturer","DeviceID","PNPDeviceID"}.Select(n=>(string)Get(port,n)??""));
            return description.IndexOf("CH340",StringComparison.OrdinalIgnoreCase)>=0
                ||description.IndexOf("VID_1A86&PID_7523",StringComparison.OrdinalIgnoreCase)>=0;
        }
        public static object BluetoothQuickPick(object target)
        {
            var selected=Get(target,"SelectedBluetoothPort");
            if(Get(selected,"CanConnect") is bool capable && capable)return selected;
            return target.GetType().GetMethod("SelectPreferredBluetoothPortLegacy",Flags).Invoke(target,null);
        }
        public static async Task BluetoothDialog(object dialog)
        {
            await ((Task)dialog.GetType().GetMethod("SelectBluetoothLegacyAsync",Flags).Invoke(dialog,null));
            var target=Get(dialog,"BluetoothTarget");
            if(target!=null && Get(target,"SelectedBluetoothPort")==null)
                Set(dialog,"IsBluetoothAdvancedExpanded",true);
        }
        public static async ValueTask<bool> Connect(object target,int mode)
        {
            if(mode!=0)
            {
                if(Get(target,"AutoConnectEnabled") is bool enabled && !enabled)return false;
                return await Legacy(target,mode).ConfigureAwait(false);
            }
            switch(target.GetType().Name)
            {
                case "SerialOutputTarget":
                    var selected=Get(target,"SelectedSerialPort");
                    if(selected==null)
                    {
                        await ((Task)target.GetType().GetMethod("RefreshPorts",Flags).Invoke(target,null)).ConfigureAwait(false);
                        selected=Get(target,"SelectedSerialPort");
                        var ports=((IEnumerable)Get(target,"SerialPorts")).Cast<object>().Where(SerialVisible).ToArray();
                        if(selected==null && ports.Length==1){selected=ports[0];Set(target,"SelectedSerialPort",selected);}
                        if(selected==null)throw new InvalidOperationException(ports.Length==0
                            ?"未找到可用串口。请在 Windows 设备管理器确认 COM 端口和对应的 USB 驱动，再点击刷新。ESP32 的 CP210x、CH34x 和原生 USB 串口均可手动选择。"
                            :"找到多个串口，请先在 USB 串口设置中选中设备对应的 COM 端口，再点击连接。");
                    }
                    if(!SerialVisible(selected))throw new InvalidOperationException("所选串口没有有效的 COM 端口，请刷新后重新选择。");
                    Log.Info("Manual serial connection uses the selected COM port without the vendor-specific #isOSR handshake");
                    return true;
                case "BluetoothOutputTarget":
                    var bluetooth=Get(target,"SelectedBluetoothPort");
                    if(bluetooth!=null)
                    {
                        if(!(Get(bluetooth,"CanConnect") is bool canConnect) || !canConnect)
                            throw new InvalidOperationException("所选蓝牙设备未提供可连接的串口服务。请确认设备使用经典蓝牙 SPP，或兼容的 BLE 串口服务；必要时先在 Windows 中配对。");
                        if((Get(bluetooth,"IsBleConnectable") is true || Get(bluetooth,"IsClassicRfcomm") is true) && BluetoothDependencyError.Value is string error)
                            throw new InvalidOperationException(error);
                        Log.Info("Manual Bluetooth connection uses only the selected device");
                        return true;
                    }
                    if(BluetoothDependencyError.Value is string dependencyError)
                        throw new InvalidOperationException(dependencyError);
                    if(await Legacy(target,mode).ConfigureAwait(false))return true;
                    throw new InvalidOperationException("未选中可连接的蓝牙设备。请刷新列表并选择自己的设备；检查 Windows 蓝牙适配器和设备的 SPP/BLE 固件。");
                case "UdpOutputTarget":
                    var endpoint=Get(target,"Endpoint") as EndPoint;
                    if(endpoint is DnsEndPoint dns)
                    {
                        if(string.IsNullOrWhiteSpace(dns.Host)||dns.Port<1)throw new InvalidOperationException("请填写设备的局域网 IPv4 地址和 UDP 端口。");
                        IPAddress[] addresses;
                        try{addresses=await Dns.GetHostAddressesAsync(dns.Host).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);}
                        catch(Exception ex) when(ex is SocketException||ex is TimeoutException){throw new InvalidOperationException("无法解析设备地址。请在 WiFi 高级连接中填写设备实际的局域网 IPv4 地址，不要依赖 tcode.local。",ex);}
                        if(!addresses.Any(a=>a.AddressFamily==AddressFamily.InterNetwork))throw new InvalidOperationException("此 UDP 连接需要设备的 IPv4 地址。");
                    }
                    else if(endpoint is not IPEndPoint ip || ip.Port<1 || ip.AddressFamily!=AddressFamily.InterNetwork)
                        throw new InvalidOperationException("请填写有效的设备 IPv4 地址和 UDP 端口。");
                    Log.Info("Manual UDP opens the configured endpoint without requiring a D1 reply; UDP has no delivery acknowledgement");
                    return true;
                default:return await Legacy(target,mode).ConfigureAwait(false);
            }
        }
    }
}
