using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using JXOPlayer.Common;
using JXOPlayer.Features;
using JXOPlayer.Script;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Reproduce reported files by reading metadata/scripts only; never start the
// main application, a visible media player, or any output transport.
public static class CheckReportedMedia
{
    public static int Main(string[] args)
    {
        try
        {
            string app=args[0];
            var config=JObject.Parse(File.ReadAllText(Path.Combine(app,"JXOPlayer.config.json")));
            var deviceType=typeof(DeviceAxis).Assembly.GetType("JXOPlayer.Common.DeviceSettings");
            var device=config["Devices"].First(x=>(string)x["Name"]=="TCode-0.3").ToObject(deviceType);
            typeof(DeviceAxis).GetMethod("InitializeFromDevice",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new[]{device});
            var cache=JsonConvert.DeserializeObject<LibraryCache>(File.ReadAllText(Path.Combine(app,"Source","Results","library-scan-benchmark.cache.json")));
            string log=File.ReadAllText(Path.Combine(app,"Logs","application.log"));
            var files=Regex.Matches(log,"Duration pairing \\[Unknown, Video: \"([^\"]+)\"").Cast<Match>().Select(m=>m.Groups[1].Value)
                .Where(p=>Path.GetExtension(p).Equals(".mp3",StringComparison.OrdinalIgnoreCase) && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(10).ToArray();
            if(files.Length==0)throw new Exception("No reported existing MP3 cases found");
            int passed=0;
            using(var matcher=new LocalDurationMatcher())foreach(string file in files)
            {
                string name=Path.GetFileNameWithoutExtension(file);
                var scripts=cache.Files.Keys.Where(p=>File.Exists(p) && (Path.GetFileName(p).Equals(name+".funscript",StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(p).Equals(name+".stroke.funscript",StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Equals(name+".L0.funscript",StringComparison.OrdinalIgnoreCase))).ToArray();
                var candidates=scripts.Select(p=>FunscriptReader.Default.FromPath(p)).Where(r=>r!=null && r.IsSuccess).ToList();
                double duration=MediaDurationReader.Read(file,CancellationToken.None);
                var result=matcher.Filter(Path.GetFileName(file),Path.GetDirectoryName(file),candidates);
                if(duration<=0 || result.Count==0)throw new Exception("Reported case failed: "+(passed+1));
                passed++;Console.WriteLine($"PASS reported MP3 case {passed}: duration {duration:0.000}s, matched primary script");
            }
            Console.WriteLine($"COMPLETE {passed} previously failing real audio files; metadata/script reads only");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
