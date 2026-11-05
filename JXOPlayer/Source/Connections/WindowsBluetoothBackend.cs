using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace JXOPlayer.Compatibility
{
    // Managed replacement for the original debug-only native Bluetooth ABI.
    // The existing application retains responsibility for motion and TCode framing.
    public static class WindowsBluetoothBackend
    {
        static readonly Guid Ffe0=Guid.Parse("0000ffe0-0000-1000-8000-00805f9b34fb");
        static readonly Guid Ffe1=Guid.Parse("0000ffe1-0000-1000-8000-00805f9b34fb");
        static readonly Guid Nus=Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
        static readonly Guid NusRx=Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
        static readonly Guid NusTx=Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
        static readonly ConcurrentDictionary<long,Port> Ports=new();
        static readonly object ScanLock=new();
        static readonly Dictionary<int,List<Device>> Cache=new();
        static long nextHandle;
        static Guid targetService=Ffe0;
        static string scanError="";
        sealed class Device { public int Transport;public string Id,Name,PortName; }
        sealed class Port
        {
            public readonly object Sync=new();
            public readonly ConcurrentQueue<byte> Received=new();
            public CancellationTokenSource Lifetime=new();
            public string Name="",Error="";
            public IntPtr ErrorPointer;
            public volatile bool IsOpen;
            public bool Destroyed,Opening;
            public int WriteTimeout=3000,ReadTimeout=1000,Capacity=65536;
            public BluetoothLEDevice Ble;
            public GattDeviceService Service;
            public GattCharacteristic WriteCharacteristic,NotifyCharacteristic;
            public GattSession Session;
            public StreamSocket Socket;
            public RfcommDeviceService Rfcomm;
            public DataReader Reader;
            public DataWriter Writer;
        }
        public static string GetAvailabilityError()
        {
            try{_ = typeof(BluetoothLEDevice).FullName;return null;}
            catch(Exception ex){return "Windows 蓝牙接口加载失败："+ex.Message;}
        }
        public static string GetScanError()=>scanError;
        public static void DecorateResponse(object response)
        {
            var type=response.GetType();
            if((string)type.GetProperty("Action")?.GetValue(response)=="scan" && !string.IsNullOrEmpty(scanError)){
                type.GetProperty("Ok")?.SetValue(response,false);type.GetProperty("Error")?.SetValue(response,scanError);
            }
        }
        static Port Find(IntPtr handle)=>Ports.TryGetValue(handle.ToInt64(),out var p)?p:null;
        static async Task<T> Timed<T>(IAsyncOperation<T> operation,CancellationToken token)
            =>await operation.AsTask(token).WaitAsync(token).ConfigureAwait(false);
        static CancellationTokenSource Deadline(Port p,int milliseconds)
        {
            var timeout=CancellationTokenSource.CreateLinkedTokenSource(p.Lifetime.Token);
            timeout.CancelAfter(milliseconds);return timeout;
        }
        static string Explain(Exception ex)=>ex is OperationCanceledException?"蓝牙操作超时或已取消。请确认设备已开机、未被其他程序占用，并检查配对及固件服务。":ex.Message;
        static void Error(Port p,Exception ex){lock(p.Sync)p.Error=Explain(ex);}
        public static IntPtr CreateSerialPort(){var id=Interlocked.Increment(ref nextHandle);Ports[id]=new Port();return new IntPtr(id);}
        public static void DestroySerialPort(IntPtr handle)
        {
            var existing=Find(handle);if(existing!=null)lock(existing.Sync)existing.Destroyed=true;
            Close(handle);
            if(Ports.TryRemove(handle.ToInt64(),out var p)){lock(p.Sync){if(p.ErrorPointer!=IntPtr.Zero)Marshal.FreeCoTaskMem(p.ErrorPointer);p.ErrorPointer=IntPtr.Zero;}}
        }
        public static void SetPortName(IntPtr handle,string name){var p=Find(handle);if(p!=null)p.Name=name??"";}
        public static bool Open(IntPtr handle)
        {
            var p=Find(handle);if(p==null)return false;
            // All WinRT waits run away from the UI synchronization context.
            return Task.Run(async ()=>{
                bool ownsOpening=false;
                try{
                    lock(p.Sync){if(p.Destroyed)throw new ObjectDisposedException("Bluetooth port");if(p.IsOpen)return true;if(p.Opening)return false;p.Opening=true;ownsOpening=true;if(p.Lifetime.IsCancellationRequested)p.Lifetime=new CancellationTokenSource();p.Error="";}
                    using var deadline=Deadline(p,12000);
                    if(p.Name.StartsWith("BLE:",StringComparison.OrdinalIgnoreCase))await OpenBle(p,deadline.Token).ConfigureAwait(false);
                    else if(p.Name.StartsWith("RFCOMM:",StringComparison.OrdinalIgnoreCase))await OpenRfcomm(p,deadline.Token).ConfigureAwait(false);
                    else throw new InvalidOperationException("无效的蓝牙设备标识，请刷新列表后重新选择。");
                    lock(p.Sync){
                        deadline.Token.ThrowIfCancellationRequested();
                        if(p.Ble!=null && p.Ble.ConnectionStatus!=BluetoothConnectionStatus.Connected)throw new InvalidOperationException("BLE 设备在初始化期间断开，请重新连接。");
                        p.IsOpen=true;
                        if(p.Socket!=null)_=ReadRfcomm(p);
                    }
                    return true;
                }catch(Exception ex){Error(p,ex);Close(handle);return false;}
                finally{if(ownsOpening)lock(p.Sync)p.Opening=false;}
            }).GetAwaiter().GetResult();
        }
        static async Task OpenBle(Port p,CancellationToken token)
        {
            var id=p.Name.Substring(4);
            BluetoothLEDevice device;
            var hex=id.Replace(":","").Replace("-","");
            if(Regex.IsMatch(hex,"^[0-9a-fA-F]{12}$"))device=await Timed(BluetoothLEDevice.FromBluetoothAddressAsync(ulong.Parse(hex,NumberStyles.HexNumber)),token);
            else if(id.Contains("#")||id.Contains("\\"))device=await Timed(BluetoothLEDevice.FromIdAsync(id),token);
            else throw new InvalidOperationException("无效的 BLE 地址，请重新扫描并选择设备。");
            if(device==null)throw new InvalidOperationException("Windows 无法访问 BLE 设备。请检查蓝牙开关、设备供电和配对权限。");
            lock(p.Sync){if(token.IsCancellationRequested){device.Dispose();token.ThrowIfCancellationRequested();}p.Ble=device;}
            var services=await Timed(device.GetGattServicesAsync(BluetoothCacheMode.Uncached),token);
            if(services.Status!=GattCommunicationStatus.Success)throw new InvalidOperationException("BLE 服务发现失败："+services.Status);
            var selected=services.Services.FirstOrDefault(s=>s.Uuid==targetService)??services.Services.FirstOrDefault(s=>s.Uuid==Ffe0)??services.Services.FirstOrDefault(s=>s.Uuid==Nus);
            foreach(var service in services.Services)if(service!=selected)service.Dispose();
            if(selected==null)throw new InvalidOperationException("设备未提供兼容的 BLE 串口服务（FFE0 或 Nordic UART）。ESP32 固件需要提供其中一种服务。");
            lock(p.Sync){if(token.IsCancellationRequested){selected.Dispose();token.ThrowIfCancellationRequested();}p.Service=selected;}
            var characteristics=await Timed(selected.GetCharacteristicsAsync(BluetoothCacheMode.Uncached),token);
            if(characteristics.Status!=GattCommunicationStatus.Success)throw new InvalidOperationException("BLE 特征读取失败："+characteristics.Status);
            var writeId=selected.Uuid==Nus?NusRx:Ffe1;
            var write=characteristics.Characteristics.FirstOrDefault(c=>c.Uuid==writeId && Writable(c));
            if(write==null)throw new InvalidOperationException("BLE 串口缺少可写特征（FFE1 或 Nordic UART RX），不能发送脚本。");
            var notifyId=selected.Uuid==Nus?NusTx:Ffe1;
            var notify=characteristics.Characteristics.FirstOrDefault(c=>c.Uuid==notifyId && (c.CharacteristicProperties&(GattCharacteristicProperties.Notify|GattCharacteristicProperties.Indicate))!=0);
            var session=await Timed(GattSession.FromDeviceIdAsync(device.BluetoothDeviceId),token);
            lock(p.Sync){if(token.IsCancellationRequested){session?.Dispose();token.ThrowIfCancellationRequested();}p.Session=session;p.WriteCharacteristic=write;p.NotifyCharacteristic=notify;}
            if(notify!=null){
                notify.ValueChanged+=(sender,args)=>{if(!ReferenceEquals(p.Ble,device))return;using var reader=DataReader.FromBuffer(args.CharacteristicValue);var bytes=new byte[reader.UnconsumedBufferLength];reader.ReadBytes(bytes);Enqueue(p,bytes);};
                var mode=(notify.CharacteristicProperties&GattCharacteristicProperties.Notify)!=0?GattClientCharacteristicConfigurationDescriptorValue.Notify:GattClientCharacteristicConfigurationDescriptorValue.Indicate;
                var status=await Timed(notify.WriteClientCharacteristicConfigurationDescriptorAsync(mode),token);
                if(status!=GattCommunicationStatus.Success)throw new InvalidOperationException("BLE 通知订阅失败："+status);
            }
            device.ConnectionStatusChanged+=(sender,args)=>{if(ReferenceEquals(p.Ble,sender)&&sender.ConnectionStatus==BluetoothConnectionStatus.Disconnected)p.IsOpen=false;};
        }
        static bool Writable(GattCharacteristic c)=>(c.CharacteristicProperties&(GattCharacteristicProperties.Write|GattCharacteristicProperties.WriteWithoutResponse))!=0;
        static async Task OpenRfcomm(Port p,CancellationToken token)
        {
            var id=p.Name.Substring(7);
            if(!id.Contains("#")&&!id.Contains("\\"))throw new InvalidOperationException("无效的经典蓝牙设备标识，请在 Windows 配对后重新扫描。");
            var service=await Timed(RfcommDeviceService.FromIdAsync(id),token);
            if(service==null)throw new InvalidOperationException("无法访问经典蓝牙 SPP 服务，请先在 Windows 配对。");
            if(service.ServiceId.Uuid!=RfcommServiceId.SerialPort.Uuid){service.Dispose();throw new InvalidOperationException("所选设备未提供经典蓝牙 SPP 串口服务。");}
            lock(p.Sync){if(token.IsCancellationRequested){service.Dispose();token.ThrowIfCancellationRequested();}p.Rfcomm=service;p.Socket=new StreamSocket();}
            await p.Socket.ConnectAsync(service.ConnectionHostName,service.ConnectionServiceName,SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication).AsTask(token).WaitAsync(token).ConfigureAwait(false);
            lock(p.Sync){token.ThrowIfCancellationRequested();p.Reader=new DataReader(p.Socket.InputStream){InputStreamOptions=InputStreamOptions.Partial};p.Writer=new DataWriter(p.Socket.OutputStream);}
        }
        static async Task ReadRfcomm(Port p)
        {
            var lifetime=p.Lifetime;var reader=p.Reader;
            try{while(!lifetime.IsCancellationRequested){var count=await Timed(reader.LoadAsync(1024),lifetime.Token);if(count==0)break;var bytes=new byte[count];reader.ReadBytes(bytes);if(!lifetime.IsCancellationRequested)Enqueue(p,bytes);}}
            catch(Exception ex){if(!lifetime.IsCancellationRequested)Error(p,ex);}
            finally{if(ReferenceEquals(p.Lifetime,lifetime))p.IsOpen=false;}
        }
        static void Enqueue(Port p,byte[] bytes){if(p.Lifetime.IsCancellationRequested)return;foreach(var b in bytes){if(p.Received.Count>=p.Capacity)p.Received.TryDequeue(out _);p.Received.Enqueue(b);}}
        public static void Close(IntPtr handle)
        {
            var p=Find(handle);if(p==null)return;
            p.IsOpen=false;p.Lifetime.Cancel();
            lock(p.Sync){
                foreach(var disposable in new IDisposable[]{p.Reader,p.Writer,p.Socket,p.Rfcomm,p.Service,p.Session,p.Ble})try{disposable?.Dispose();}catch{}
                p.Reader=null;p.Writer=null;p.Socket=null;p.Rfcomm=null;p.Service=null;p.Session=null;p.Ble=null;p.WriteCharacteristic=null;p.NotifyCharacteristic=null;
                while(p.Received.TryDequeue(out _)){}
            }
        }
        public static int Write(IntPtr handle,byte[] buffer,int offset,int count)
        {
            var p=Find(handle);if(p==null||!p.IsOpen)return -1;
            if(buffer==null||offset<0||count<0||offset>buffer.Length-count){p.Error="蓝牙发送缓冲区范围无效";return -1;}
            lock(p.Sync){try{
                if(!p.IsOpen)return -1;
                using var deadline=Deadline(p,p.WriteTimeout);
                if(p.WriteCharacteristic!=null){
                    int packet=Math.Max(1,Math.Min(512,GetBleMtuSize(handle)-3));
                    var option=(p.WriteCharacteristic.CharacteristicProperties&GattCharacteristicProperties.WriteWithoutResponse)!=0?GattWriteOption.WriteWithoutResponse:GattWriteOption.WriteWithResponse;
                    for(int sent=0;sent<count;sent+=packet){var size=Math.Min(packet,count-sent);var bytes=new byte[size];Array.Copy(buffer,offset+sent,bytes,0,size);using var writer=new DataWriter();writer.WriteBytes(bytes);
                        var result=Timed(p.WriteCharacteristic.WriteValueAsync(writer.DetachBuffer(),option),deadline.Token).GetAwaiter().GetResult();
                        if(result!=GattCommunicationStatus.Success)throw new InvalidOperationException("BLE 发送失败："+result);
                    }
                }else{var bytes=new byte[count];Array.Copy(buffer,offset,bytes,0,count);p.Writer.WriteBytes(bytes);var sent=Timed(p.Writer.StoreAsync(),deadline.Token).GetAwaiter().GetResult();if(sent!=count)throw new InvalidOperationException("经典蓝牙发送不完整");}
                return count;
            }catch(Exception ex){p.Error=Explain(ex);p.IsOpen=false;p.Lifetime.Cancel();return -1;}}
        }
        public static int Read(IntPtr handle,byte[] buffer,int offset,int count)
        {
            var p=Find(handle);if(p==null||buffer==null||offset<0||count<0||offset>buffer.Length-count)return -1;
            int read=0;while(read<count&&p.Received.TryDequeue(out var value))buffer[offset+read++]=value;return read;
        }
        public static bool GetIsOpen(IntPtr handle)=>Find(handle)?.IsOpen??false;
        public static int GetBytesToRead(IntPtr handle)=>Find(handle)?.Received.Count??0;
        public static int GetBytesToWrite(IntPtr handle)=>0; // Writes complete synchronously; no application queue.
        public static int GetBleMtuSize(IntPtr handle)=>Find(handle)?.Session?.MaxPduSize??23;
        public static IntPtr GetLastNativeError(IntPtr handle)
        {
            var p=Find(handle);if(p==null)return IntPtr.Zero;
            lock(p.Sync){if(p.ErrorPointer!=IntPtr.Zero)Marshal.FreeCoTaskMem(p.ErrorPointer);p.ErrorPointer=Marshal.StringToCoTaskMemUTF8(p.Error);return p.ErrorPointer;}
        }
        // Serial line parameters do not apply to GATT/SPP streams. Bluetooth COM
        // transport continues to use the original independent SerialPort backend.
        public static void SetBaudRate(IntPtr h,int value){}
        public static void SetParity(IntPtr h,int value){}
        public static void SetStopBits(IntPtr h,int value){}
        public static void SetDataBits(IntPtr h,int value){}
        public static void SetHandshake(IntPtr h,int value){}
        public static void SetDtrEnable(IntPtr h,bool value){}
        public static void SetRtsEnable(IntPtr h,bool value){}
        public static void SetReadTimeout(IntPtr h,int value){var p=Find(h);if(p!=null)p.ReadTimeout=Math.Clamp(value,1,30000);}
        public static void SetWriteTimeout(IntPtr h,int value){var p=Find(h);if(p!=null)p.WriteTimeout=Math.Clamp(value,100,5000);}
        public static void SetReadBufferSize(IntPtr h,int value){var p=Find(h);if(p!=null)p.Capacity=Math.Clamp(value,1024,1048576);}
        public static void SetWriteBufferSize(IntPtr h,int value){}
        public static void SetNativeLogCallback(object callback){}
        public static void SetBleTargetDeviceName(string name){} // Device names never establish protocol compatibility.
        public static void SetBleTargetServiceUuid(string uuid){if(Guid.TryParse(uuid,out var parsed))targetService=parsed;}
        public static void ClearBleTargetServiceUuid(){targetService=Ffe0;}
        public static int GetBluetoothDevices(int transport,Array devices,int maxCount)=>Enumerate(transport,devices,maxCount,false);
        public static int GetCachedBluetoothDevices(int transport,Array devices,int maxCount)=>Enumerate(transport,devices,maxCount,true);
        static int Enumerate(int transport,Array devices,int maxCount,bool cached)
        {
            lock(ScanLock){
                if(devices==null||maxCount<=0){try{Cache[transport]=Task.Run(()=>Scan(transport,cached)).GetAwaiter().GetResult();scanError="";}catch(Exception ex){scanError=Explain(ex);Cache[transport]=new();}return Cache[transport].Count;}
                if(!Cache.TryGetValue(transport,out var found))return 0;
                int count=Math.Min(found.Count,Math.Min(devices.Length,maxCount));var type=devices.GetType().GetElementType();
                for(int i=0;i<count;i++){var item=Activator.CreateInstance(type);type.GetField("TransportType").SetValue(item,Enum.ToObject(type.GetField("TransportType").FieldType,found[i].Transport));type.GetField("DeviceId").SetValue(item,found[i].Id);type.GetField("DisplayName").SetValue(item,found[i].Name);type.GetField("PortName").SetValue(item,found[i].PortName);devices.SetValue(item,i);}return count;
            }
        }
        static async Task<List<Device>> Scan(int transport,bool cached)
        {
            using var timeout=new CancellationTokenSource(6000);var token=timeout.Token;
            var adapter=await Timed(BluetoothAdapter.GetDefaultAsync(),token);
            if(adapter==null)throw new InvalidOperationException("未找到 Windows 蓝牙适配器，请检查蓝牙硬件和驱动。");
            var radio=await Timed(adapter.GetRadioAsync(),token);
            if(radio!=null&&radio.State!=Windows.Devices.Radios.RadioState.On)throw new InvalidOperationException("Windows 蓝牙处于关闭或禁用状态，请先打开蓝牙再刷新。");
            var found=new ConcurrentDictionary<string,Device>(StringComparer.OrdinalIgnoreCase);
            if(transport==0||transport==1){
                var infos=await Timed(DeviceInformation.FindAllAsync(RfcommDeviceService.GetDeviceSelector(RfcommServiceId.SerialPort)),token);
                foreach(var info in infos)found[info.Id]=new Device{Transport=1,Id="RFCOMM:"+info.Id,Name=info.Name,PortName="RFCOMM"};
            }
            if(transport==0||transport==2){
                var infos=await Timed(DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)),token);
                foreach(var info in infos)found[info.Id]=new Device{Transport=2,Id="BLE:"+info.Id,Name=info.Name,PortName="BLE"};
                if(!cached){
                    var watcher=new BluetoothLEAdvertisementWatcher{ScanningMode=BluetoothLEScanningMode.Active};
                    TypedEventHandler<BluetoothLEAdvertisementWatcher,BluetoothLEAdvertisementReceivedEventArgs> received=(s,e)=>{
                        var name=e.Advertisement.LocalName;
                        // Retain advertisements with serial service IDs even if unnamed.
                        if(string.IsNullOrWhiteSpace(name)&&!e.Advertisement.ServiceUuids.Any(g=>g==Ffe0||g==Nus||g==targetService))return;
                        var id="BLE:"+e.BluetoothAddress.ToString("X12");found[id]=new Device{Transport=2,Id=id,Name=string.IsNullOrWhiteSpace(name)?"BLE 串口设备 "+e.BluetoothAddress.ToString("X12"):name,PortName="BLE"};
                    };
                    watcher.Received+=received;
                    try{watcher.Start();await Task.Delay(2500,token);}
                    finally{watcher.Stop();watcher.Received-=received;}
                }
            }
            return found.Values.OrderBy(d=>d.Name,StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
