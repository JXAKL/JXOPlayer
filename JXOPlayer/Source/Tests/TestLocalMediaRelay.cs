using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using JXOPlayer.Features;

public static class TestLocalMediaRelay
{
    public static async Task Run(string root,Action<bool,string> check)
    {
        string path=Path.Combine(root,"relay-test.mp4");byte[] data=Enumerable.Range(0,4096).Select(i=>(byte)(i%251)).ToArray();File.WriteAllBytes(path,data);
        using(var handler=new HttpClientHandler{UseProxy=false})
        using(var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(4)})
        {
            string url=LocalMediaRelay.Register(path);
            try
            {
                check(new Uri(url).Host=="127.0.0.1" && PlaybackLibrary.SamePath(url,path),"relay URL is loopback-only and identifies the original file");
                check(LocalMediaRelay.Register(path)==url,"unchanged media reuses its registered route");
                check((await client.GetByteArrayAsync(url)).SequenceEqual(data),"relay streams exact bytes without changing media content");
                using(var head=await client.SendAsync(new HttpRequestMessage(HttpMethod.Head,url)))
                    check(head.StatusCode==HttpStatusCode.OK && head.Content.Headers.ContentLength==data.Length && (await head.Content.ReadAsByteArrayAsync()).Length==0,"HEAD reports length without a body");
                async Task<byte[]> Range(string value,HttpStatusCode expected)
                {
                    using(var request=new HttpRequestMessage(HttpMethod.Get,url))
                    {
                        request.Headers.TryAddWithoutValidation("Range",value);
                        using(var response=await client.SendAsync(request))
                        {check(response.StatusCode==expected,"range status: "+value);return await response.Content.ReadAsByteArrayAsync();}
                    }
                }
                check((await Range("bytes=100-199",HttpStatusCode.PartialContent)).SequenceEqual(data.Skip(100).Take(100)),"seeking returns exactly the requested range");
                check((await Range("bytes=-17",HttpStatusCode.PartialContent)).SequenceEqual(data.TakeLast(17)),"suffix reads locate media tail metadata");
                check((await Range("bytes=4000-",HttpStatusCode.PartialContent)).SequenceEqual(data.Skip(4000)),"open-ended range streams remaining bytes");
                await Range("bytes=5000-",HttpStatusCode.RequestedRangeNotSatisfiable);
                await Range("bytes=0-1,3-4",HttpStatusCode.RequestedRangeNotSatisfiable);
                using(var unknown=await client.GetAsync(new Uri(new Uri(url),"/unregistered.mp4")))check(unknown.StatusCode==HttpStatusCode.NotFound,"unregistered routes cannot read other files");
                using(var wrong=await client.PostAsync(url,new StringContent("x")))check(wrong.StatusCode==HttpStatusCode.MethodNotAllowed,"relay refuses mutation requests");
                check(LocalMediaRelay.TryRange("bytes=4294967296-",6000000000,out var start,out var end) && start==4294967296 && end==5999999999,"range math supports files larger than four GiB");
                File.WriteAllBytes(path,new byte[20]);
                using(var changed=await client.GetAsync(url))check(changed.StatusCode==HttpStatusCode.Conflict,"changed media does not silently reuse an old stream registration");
            }
            finally{LocalMediaRelay.Stop();}
            bool closed=false;try{await client.GetAsync(url);}catch(HttpRequestException){closed=true;}
            check(closed && LocalMediaRelay.Resolve(url)==url,"stopping relay closes listener and removes path mappings");
        }
    }
}
