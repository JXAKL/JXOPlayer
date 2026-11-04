namespace JXOPlayer.Features
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;

    public static class MotionIdentity
    {
        public static string Compute(IEnumerable<KeyValuePair<double,double>> points)
        {
            using(var stream=new MemoryStream())
            {
                using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))
                    foreach(var point in points)
                    {
                        // Same units as native keyframes: seconds and 0..1.
                        writer.Write(Math.Round(point.Key,6));writer.Write(Math.Round(point.Value,9));
                    }
                return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
            }
        }
    }
}
