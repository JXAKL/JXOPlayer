using System;
using System.Diagnostics;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using JXOPlayer.Compatibility;

public static class WindowsBluetoothRegression
{
    public static async Task Run(Action<bool,string> check)
    {
        check(!File.Exists(Path.Combine(AppContext.BaseDirectory,"BLEDriverCore.dll")),"test runs with old debug-only Bluetooth DLL absent");
        check(WindowsBluetoothBackend.GetAvailabilityError()==null,"Windows Bluetooth projection loads without debug runtime");
        using(var writer=new Windows.Storage.Streams.DataWriter()){
            writer.WriteBytes(new byte[]{0,127,128,255});
            using var reader=Windows.Storage.Streams.DataReader.FromBuffer(writer.DetachBuffer());
            var bytes=new byte[4];reader.ReadBytes(bytes);
            check(bytes[0]==0&&bytes[1]==127&&bytes[2]==128&&bytes[3]==255,"Windows Bluetooth buffer projection preserves raw byte values");
        }
        var native=Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory,"JXOPlayer.dll")).GetType("NativeBluetoothSerialPort",true);
        var flags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        object Call(string name,params object[] args)=>native.GetMethod(name,flags).Invoke(null,args);
        var handle=(IntPtr)Call("CreateSerialPort");
        try{
            check(handle!=IntPtr.Zero&&!(bool)Call("GetIsOpen",handle),"patched original Bluetooth ABI creates disconnected port");
            Call("SetPortName",handle,"BLE:INVALID");
            var watch=Stopwatch.StartNew();
            check(!(bool)Call("Open",handle)&&watch.Elapsed<TimeSpan.FromSeconds(2),"invalid BLE identity fails promptly without contacting hardware");
            var message=(string)Call("GetLastErrorString",handle);
            check(message.Contains("无效"),"original caller receives useful managed Bluetooth error");
            check((int)Call("Write",handle,new byte[]{1},0,1)==-1,"disconnected port rejects writes");
            Call("Close",handle);Call("Close",handle);
            check(!(bool)Call("GetIsOpen",handle),"repeated Bluetooth close is harmless");
        }finally{Call("DestroySerialPort",handle);}
        check(!(bool)Call("Open",handle),"destroyed handle cannot reopen");
        for(int i=0;i<25;i++){
            var cycle=(IntPtr)Call("CreateSerialPort");Call("SetPortName",cycle,"BLE:INVALID");
            if((bool)Call("Open",cycle))throw new Exception("Invalid device opened");Call("DestroySerialPort",cycle);
        }
        check(true,"25 failed open/dispose cycles complete without stale handles");
        // Pass actual application struct arrays across the replacement ABI.
        var backend=typeof(WindowsBluetoothBackend);
        var deviceType=backend.GetNestedType("Device",BindingFlags.NonPublic);
        var cached=(IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(deviceType));
        var device=Activator.CreateInstance(deviceType,true);
        deviceType.GetField("Transport").SetValue(device,2);deviceType.GetField("Id").SetValue(device,"BLE:001122334455");deviceType.GetField("Name").SetValue(device,"测试 ESP32 日本語 🎮");deviceType.GetField("PortName").SetValue(device,"BLE");cached.Add(device);
        var cache=(IDictionary)backend.GetField("Cache",flags).GetValue(null);cache[42]=cached;
        try{
            var enumerate=native.GetMethod("GetBluetoothDevices",flags);var signature=enumerate.GetParameters();
            var array=Array.CreateInstance(signature[1].ParameterType.GetElementType(),2);
            var count=(int)enumerate.Invoke(null,new object[]{Enum.ToObject(signature[0].ParameterType,42),array,2});
            var item=array.GetValue(0);
            check(count==1 && (string)item.GetType().GetField("DisplayName").GetValue(item)=="测试 ESP32 日本語 🎮","native device struct array preserves Unicode names through managed ABI");
        }finally{cache.Remove(42);}
        // Exercise the real helper process and its independently patched assembly.
        using var process=new Process{StartInfo=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"BtBridge.exe")){
            WorkingDirectory=AppContext.BaseDirectory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true
        }};
        process.Start();var errors=process.StandardError.ReadToEndAsync();
        async Task<JsonDocument> Command(object command){await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));await process.StandardInput.FlushAsync();var line=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));if(line==null)throw new Exception("Bluetooth helper exited: "+await errors);return JsonDocument.Parse(line.TrimStart('\ufeff'));}
        try{
            using(var response=await Command(new{action="configure",bleTargetServiceUuid="0000FFE0-0000-1000-8000-00805F9B34FB"}))check(response.RootElement.GetProperty("ok").GetBoolean(),"real Bluetooth helper configures without native DLL");
            using(var response=await Command(new{action="connect",deviceId="BLE:INVALID"}))check(!response.RootElement.GetProperty("ok").GetBoolean(),"real Bluetooth helper rejects invalid connection and survives");
            using(var response=await Command(new{action="close"}))check(response.RootElement.GetProperty("ok").GetBoolean(),"real Bluetooth helper handles disconnect after failed open");
            using(var response=await Command(new{action="scan",cachedOnly=true,transportType="ble"}))check(response.RootElement.GetProperty("action").GetString()=="scan","real Windows Bluetooth enumeration completes without old DLL");
        }finally{
            process.StandardInput.Close();try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}catch(TimeoutException){process.Kill();await process.WaitForExitAsync();}
        }
        check(!(await errors).Contains("DllNotFoundException"),"helper process has no missing native dependency errors");
    }
}
