namespace JXOPlayer.Features
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    // Compatibility transport for players whose filesystem interface is limited
    // to MAX_PATH. Only explicitly registered videos are reachable, on loopback,
    // through unpredictable routes. No directory listing or arbitrary path input.
    public static class LocalMediaRelay
    {
        sealed class Media { public string Path,Url;public long Length,Modified; }
        static readonly object gate=new object();
        static readonly Dictionary<string,Media> routes=new Dictionary<string,Media>(StringComparer.Ordinal);
        static readonly Dictionary<string,Media> urls=new Dictionary<string,Media>(StringComparer.Ordinal);
        static TcpListener listener;
        static CancellationTokenSource lifetime;
        static readonly SemaphoreSlim connections=new SemaphoreSlim(8,8);
        public static string Resolve(string value)
        {
            if(value==null)return null;
            lock(gate)return urls.TryGetValue(value,out var media)?media.Path:value;
        }
        public static string Register(string path)
        {
            var info=new FileInfo(path);
            if(!info.Exists || !PlaybackLibrary.IsVideo(path))throw new IOException("媒体不存在或不是视频文件");
            lock(gate)
            {
                if(listener==null)
                {
                    listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
                    lifetime=new CancellationTokenSource();_ = AcceptAsync(listener,lifetime.Token);
                }
                foreach(var entry in urls.Values)
                    if(string.Equals(entry.Path,path,StringComparison.OrdinalIgnoreCase) && entry.Length==info.Length && entry.Modified==info.LastWriteTimeUtc.Ticks)return entry.Url;
                string route="/"+Guid.NewGuid().ToString("N")+Path.GetExtension(path).ToLowerInvariant();
                string url="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+route;
                var media=new Media{Path=path,Url=url,Length=info.Length,Modified=info.LastWriteTimeUtc.Ticks};
                routes.Add(route,media);urls.Add(url,media);return url;
            }
        }
        public static void Stop()
        {
            lock(gate){lifetime?.Cancel();listener?.Stop();listener=null;lifetime?.Dispose();lifetime=null;routes.Clear();urls.Clear();}
        }
        static async Task AcceptAsync(TcpListener server,CancellationToken token)
        {
            try
            {
                while(!token.IsCancellationRequested)
                {
                    var client=await server.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    if(!connections.Wait(0)){client.Dispose();continue;}
                    _ = ServeAsync(client,token);
                }
            }
            catch(Exception ex) when(ex is OperationCanceledException || ex is ObjectDisposedException || ex is SocketException){}
        }
        static async Task ServeAsync(TcpClient client,CancellationToken token)
        {
            try
            {
                using(client)
                using(var stream=client.GetStream())
                {
                    // Bounded headers and deadlines also cover clients that never
                    // finish a request. Read only through CRLFCRLF, no request body.
                    var header=new List<byte>();var one=new byte[1];
                    var deadline=DateTime.UtcNow.AddSeconds(5);
                    while(header.Count<16384)
                    {
                        var remaining=deadline-DateTime.UtcNow;if(remaining<=TimeSpan.Zero)return;
                        int read=await stream.ReadAsync(one.AsMemory(),token).AsTask().WaitAsync(remaining,token).ConfigureAwait(false);
                        if(read==0)return;header.Add(one[0]);int n=header.Count;
                        if(n>=4 && header[n-4]==13 && header[n-3]==10 && header[n-2]==13 && header[n-1]==10)break;
                    }
                    string text=Encoding.ASCII.GetString(header.ToArray());
                    if(!text.EndsWith("\r\n\r\n",StringComparison.Ordinal))return;
                    string[] lines=text.Split(new[]{"\r\n"},StringSplitOptions.None);
                    string[] first=lines[0].Split(' ');
                    if(first.Length!=3 || (first[0]!="GET" && first[0]!="HEAD")){await Error(stream,405,"Method Not Allowed",token);return;}
                    Media media;lock(gate)routes.TryGetValue(first[1],out media);
                    if(media==null){await Error(stream,404,"Not Found",token);return;}
                    var info=new FileInfo(media.Path);
                    if(!info.Exists || info.Length!=media.Length || info.LastWriteTimeUtc.Ticks!=media.Modified){await Error(stream,409,"Conflict",token);return;}
                    string range=null;
                    foreach(string line in lines)
                        if(line.StartsWith("Range:",StringComparison.OrdinalIgnoreCase))
                        {if(range!=null){await Error(stream,400,"Bad Request",token);return;}range=line.Substring(6).Trim();}
                    if(!TryRange(range,media.Length,out long start,out long end))
                    {await Header(stream,"416 Range Not Satisfiable","Content-Range: bytes */"+media.Length+"\r\nContent-Length: 0\r\n",token);return;}
                    long count=end-start+1;
                    string details="Accept-Ranges: bytes\r\nContent-Type: application/octet-stream\r\nContent-Length: "+count+"\r\n";
                    if(range!=null)details+="Content-Range: bytes "+start+"-"+end+"/"+media.Length+"\r\n";
                    using(var file=new FileStream(media.Path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan))
                    {
                        if(file.Length!=media.Length){await Error(stream,409,"Conflict",token);return;}
                        await Header(stream,range==null?"200 OK":"206 Partial Content",details,token);
                        if(first[0]=="HEAD")return;
                        file.Seek(start,SeekOrigin.Begin);var buffer=new byte[65536];
                        while(count>0)
                        {
                            int read=await file.ReadAsync(buffer.AsMemory(0,(int)Math.Min(count,buffer.Length)),token).ConfigureAwait(false);
                            if(read==0)return;
                            await stream.WriteAsync(buffer.AsMemory(0,read),token).AsTask().WaitAsync(TimeSpan.FromSeconds(10),token).ConfigureAwait(false);
                            count-=read;
                        }
                    }
                }
            }
            catch(Exception ex) when(ex is IOException || ex is SocketException || ex is OperationCanceledException || ex is TimeoutException || ex is ObjectDisposedException || ex is UnauthorizedAccessException){}
            finally{connections.Release();}
        }
        public static bool TryRange(string range,long length,out long start,out long end)
        {
            start=0;end=length-1;if(length<=0)return false;if(range==null)return true;
            if(!range.StartsWith("bytes=",StringComparison.OrdinalIgnoreCase))return false;
            string[] parts=range.Substring(6).Split('-');
            bool Number(string value,out long number)=>long.TryParse(value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out number);
            if(parts.Length!=2)return false;
            if(parts[0].Length==0)
            {if(!Number(parts[1],out long suffix) || suffix<=0)return false;start=Math.Max(0,length-suffix);return true;}
            if(!Number(parts[0],out start) || start>=length)return false;
            if(parts[1].Length>0){if(!Number(parts[1],out end) || end<start)return false;end=Math.Min(end,length-1);}
            return true;
        }
        static Task Error(NetworkStream stream,int code,string reason,CancellationToken token)=>Header(stream,code+" "+reason,"Content-Length: 0\r\n",token);
        static Task Header(NetworkStream stream,string status,string details,CancellationToken token)
        {
            byte[] bytes=Encoding.ASCII.GetBytes("HTTP/1.1 "+status+"\r\n"+details+"Connection: close\r\n\r\n");
            return stream.WriteAsync(bytes.AsMemory(),token).AsTask().WaitAsync(TimeSpan.FromSeconds(5),token);
        }
    }
}
