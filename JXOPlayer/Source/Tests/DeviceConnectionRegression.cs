using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using JXOPlayer.Compatibility;

public static class DeviceConnectionRegression
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static object Get(object o,string n)=>o.GetType().GetProperty(n,All).GetValue(o);
    static void Set(object o,string n,object v)=>o.GetType().GetProperty(n,All).SetValue(o,v);
    static object Call(object o,string n,params object[] args)=>o.GetType().GetMethod(n,All).Invoke(o,args);
    static object Port(Type t,string name,string id,string com){var p=Activator.CreateInstance(t,true);Set(p,"Name",name);Set(p,"DeviceID",id);Set(p,"PortName",com);return p;}
    static async Task<bool> Preflight(object target,int mode){var m=target.GetType().GetMethod("OnConnectingAsync",All);return await (ValueTask<bool>)m.Invoke(target,new[]{Enum.ToObject(m.GetParameters()[0].ParameterType,mode)});}
    static async Task Failure(Func<ValueTask<bool>> action,string text,Action<bool,string> check){try{await action();check(false,"expected error: "+text);}catch(InvalidOperationException ex){check(ex.Message.Contains(text),"actionable error: "+text);}}
    public static async Task Run(object[] targets,Action<bool,string> check)
    {
        var serial=targets.Single(t=>t.GetType().Name=="SerialOutputTarget");
        var st=serial.GetType();var pt=st.GetNestedType("SerialPortInfo",All);
        foreach(var name in new[]{"Silicon Labs CP210x","ESP32 USB JTAG/serial","CH343","USB 串行设备","CH340"})
        {
            var port=Port(pt,name,"USB\\TEST"+name,"COM123");
            check((bool)st.GetMethod("IsVisibleSerialPort",All).Invoke(null,new[]{port}),"native serial list accepts "+name);
            Set(serial,"SelectedSerialPort",port);
            check(await Preflight(serial,0),"manual preflight accepts "+name+" without opening hardware");
        }
        check(!(bool)st.GetMethod("IsCh340Port",All).Invoke(null,new[]{Port(pt,"CP210x","USB\\VID_10C4&PID_EA60","COM8")}),"unknown serial does not enter automatic vendor probe");
        check((bool)st.GetMethod("IsCh340Port",All).Invoke(null,new[]{Port(pt,"USB 串行设备","USB\\VID_1A86&PID_7523","COM8")}),"CH340 hardware ID remains discoverable");
        foreach(var target in targets)check(!await Preflight(target,1),target.GetType().Name+" automatic connection respects disabled setting");
        var bt=targets.Single(t=>t.GetType().Name=="BluetoothOutputTarget");var btType=bt.GetType();var bp=btType.GetNestedType("BluetoothPortInfo",All);
        var custom=Port(bp,"My ESP32","BTREG:001122334455","COM17");
        Set(custom,"TransportType",Enum.Parse(bp.GetProperty("TransportType").PropertyType,"Classic"));
        var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(bp));list.Add(custom);
        var filtered=(IList)btType.GetMethod("FilterPreferredPorts",All).Invoke(null,new object[]{list});
        check(filtered.Count==1&&ReferenceEquals(filtered[0],custom),"custom Bluetooth name remains visible and unmodified");
        check(!(bool)btType.GetMethod("IsAutomaticBluetoothCandidate",All).Invoke(null,new[]{custom}),"unknown Bluetooth excluded from automatic selection");
        Set(bt,"SelectedBluetoothPort",custom);
        check(await Preflight(bt,0),"manual Bluetooth honors selected custom device");
        check(ReferenceEquals(Call(bt,"SelectPreferredBluetoothPortForQuickConnect"),custom),"Bluetooth quick reconnect preserves user selection");
        Set(bt,"SelectedBluetoothPort",null);
        var ports=Get(bt,"BluetoothPorts");Call(ports,"Add",custom);
        check(Call(bt,"SelectPreferredBluetoothPortForQuickConnect")==null,"quick selection never chooses an unknown unselected Bluetooth device");
        Call(ports,"Clear");
        var udp=targets.Single(t=>t.GetType().Name=="UdpOutputTarget");
        using(var receiver=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)))
        {
            Set(udp,"Endpoint",receiver.Client.LocalEndPoint);
            Set(udp,"UpdateType",Enum.Parse(udp.GetType().GetProperty("UpdateType").PropertyType,"FixedUpdate"));
            foreach(var entry in (IEnumerable)Get(udp,"AxisSettings"))Set(Get(entry,"Value"),"Enabled",true);
            check(await Preflight(udp,0),"manual UDP accepts endpoint without proprietary handshake");
            check(receiver.Available==0,"manual UDP preflight sends no probe commands");
            // Exercise the real output loop against this process only, never a device.
            var connect=udp.GetType().GetMethod("ConnectAsync",All);
            try{
                await (Task)connect.Invoke(udp,new[]{Enum.ToObject(connect.GetParameters()[0].ParameterType,0)});
                await Task.Delay(150);
                check(Get(udp,"Status").ToString()=="Connected","native UDP starts real output thread");
                var packet=await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
                check(Encoding.UTF8.GetString(packet.Buffer).Contains("L0"),"native UDP output delivers TCode to local receiver without D1 reply");
                check(Get(udp,"Status").ToString()=="Connected","native UDP connection reaches connected state");
            }finally{await ((Task)Call(udp,"DisconnectAsync")).WaitAsync(TimeSpan.FromSeconds(5));}
            check(Get(udp,"Status").ToString()=="Disconnected","native UDP disconnect completes");
        }
        Set(serial,"SelectedSerialPort",null);
        await Failure(()=>ConnectionCompatibility.Connect(new Fixtures.SerialOutputTarget(),0),"未找到可用串口",check);
        var multiple=new Fixtures.SerialOutputTarget();multiple.SerialPorts.Add(new Fixtures.Port{PortName="COM1"});multiple.SerialPorts.Add(new Fixtures.Port{PortName="COM2"});
        await Failure(()=>ConnectionCompatibility.Connect(multiple,0),"多个串口",check);
        check(multiple.SelectedSerialPort==null,"multiple ports never silently select an arbitrary device");
        var single=new Fixtures.SerialOutputTarget();single.SerialPorts.Add(new Fixtures.Port{PortName="COM7"});
        check(await ConnectionCompatibility.Connect(single,0)&&single.SelectedSerialPort.PortName=="COM7","single serial port can be selected from a fresh install");
        var dependencyHint=ConnectionCompatibility.BluetoothHint(new Fixtures.Dialog());
        await Failure(()=>ConnectionCompatibility.Connect(new Fixtures.BluetoothOutputTarget(),0),dependencyHint.Contains("无法加载")?"无法加载":"未选中",check);
        await Failure(()=>ConnectionCompatibility.Connect(new Fixtures.BluetoothOutputTarget{SelectedBluetoothPort=new Fixtures.Port()},0),"串口服务",check);
        await Failure(()=>ConnectionCompatibility.Connect(new Fixtures.UdpOutputTarget{Endpoint=new IPEndPoint(IPAddress.IPv6Loopback,80)},0),"IPv4",check);
        await Failure(()=>ConnectionCompatibility.Connect(new Fixtures.UdpOutputTarget{Endpoint=new IPEndPoint(IPAddress.Loopback,0)},0),"IPv4",check);
        check(await ConnectionCompatibility.Connect(new Fixtures.UdpOutputTarget{Endpoint=new DnsEndPoint("localhost",8000)},0),"manual UDP hostname resolution accepts local IPv4");
        var automatic=new Fixtures.SerialOutputTarget{AutoConnectEnabled=true};
        check(!await ConnectionCompatibility.Connect(automatic,1)&&automatic.LegacyCalls==1,"enabled automatic mode retains legacy handshake result");
        var dialog=new Fixtures.Dialog();await ConnectionCompatibility.BluetoothDialog(dialog);
        check(dialog.IsBluetoothAdvancedExpanded,"Bluetooth card opens picker when quick selection finds no device");
        await WindowsBluetoothRegression.Run(check);
        var bridge=btType.GetField("_bridge",All).GetValue(bt);
        var scan=(Task)Call(bridge,"ScanAsync",true,"ble",System.Threading.CancellationToken.None);
        await scan.WaitAsync(TimeSpan.FromSeconds(10));
        check(Get(scan,"Result") is IEnumerable,"original application bridge scan returns typed device list");
    }
}
namespace Fixtures
{
    public enum ConnectionType{Manual,AutoConnect}
    public class Port{public string PortName{get;set;}public bool CanConnect{get;set;}}
    public class SerialOutputTarget
    {
        public Port SelectedSerialPort{get;set;}public List<Port> SerialPorts{get;}=new();public bool AutoConnectEnabled{get;set;}public int LegacyCalls;
        public Task RefreshPorts()=>Task.CompletedTask;
        public ValueTask<bool> OnConnectingLegacyAsync(ConnectionType mode){LegacyCalls++;return ValueTask.FromResult(false);}
    }
    public class BluetoothOutputTarget{public Port SelectedBluetoothPort{get;set;}public ValueTask<bool> OnConnectingLegacyAsync(ConnectionType mode)=>ValueTask.FromResult(false);}
    public class UdpOutputTarget{public EndPoint Endpoint{get;set;}}
    public class Dialog{public BluetoothOutputTarget BluetoothTarget{get;}=new();public bool IsBluetoothAdvancedExpanded{get;set;}public Task SelectBluetoothLegacyAsync()=>Task.CompletedTask;}
}
