namespace JXOPlayer.Features
{
    using System;
    using System.IO;
    using System.Threading;

    // Read container metadata only: seek over movie data, never decode frames.
    // Unsupported/fragmented/truncated containers return unknown for the fallback.
    public static class ContainerDurationReader
    {
        sealed class HeaderReader : IDisposable
        {
            public readonly FileStream Stream;
            readonly CancellationToken token;
            int elements;
            readonly System.Diagnostics.Stopwatch clock=System.Diagnostics.Stopwatch.StartNew();
            public HeaderReader(string path,CancellationToken token)
            { this.token=token;Stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,4096,FileOptions.RandomAccess); }
            public void Check()
            {
                token.ThrowIfCancellationRequested();
                if(++elements>16384 || clock.Elapsed.TotalSeconds>2)throw new InvalidDataException("Header limit");
            }
            public ulong UInt(int count)
            {
                ulong value=0;
                for(int i=0;i<count;i++){int b=Stream.ReadByte();if(b<0)throw new EndOfStreamException();value=(value<<8)|(byte)b;}
                return value;
            }
            public long End(ulong size,long limit)
            {
                if(Stream.Position>limit || size>(ulong)(limit-Stream.Position))throw new InvalidDataException("Truncated header");
                return Stream.Position+(long)size;
            }
            public ulong Vint(bool id,out bool unknown)
            {
                int first=(int)UInt(1),mask=128,count=1;
                while(mask>0 && (first&mask)==0){mask>>=1;count++;}
                if(mask==0 || (id && count>4))throw new InvalidDataException("Invalid EBML integer");
                ulong value=(ulong)(id?first:first&(mask-1));
                for(int i=1;i<count;i++)value=(value<<8)|UInt(1);
                unknown=!id && value==((1UL<<(7*count))-1);return value;
            }
            public void Dispose()=>Stream.Dispose();
        }
        public static double Read(string path,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string ext=Path.GetExtension(path).ToLowerInvariant();
            if(ext!=".mp4" && ext!=".mov" && ext!=".m4v" && ext!=".mkv" && ext!=".webm")return 0;
            try
            {
                using(var reader=new HeaderReader(path,token))
                    return ext==".mkv" || ext==".webm" ? Matroska(reader) : Mp4(reader,reader.Stream.Length,false);
            }
            catch(Exception ex) when(ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is OverflowException){return 0;}
        }
        static double Mp4(HeaderReader r,long end,bool movie)
        {
            double duration=0;bool fragmented=false;
            while(r.Stream.Position<end)
            {
                r.Check();long start=r.Stream.Position;
                if(end-start<8)return 0;
                ulong size=r.UInt(4),type=r.UInt(4);int header=8;
                if(size==1){if(end-r.Stream.Position<8)return 0;size=r.UInt(8);header=16;}
                if(size==0)size=(ulong)(end-start);
                if(size<(ulong)header)return 0;
                long next=r.End(size-(ulong)header,end);
                if(type==0x6d6f6f76 && !movie) // moov
                {
                    double found=Mp4(r,next,true);
                    if(DurationMatching.Known(found))return found;
                }
                else if(movie && type==0x6d766578)fragmented=true; // mvex: use demuxer duration
                else if(movie && type==0x6d766864 && next-r.Stream.Position>=20) // mvhd
                {
                    int version=(int)r.UInt(1);r.UInt(3);
                    if(version==0){r.UInt(8);ulong scale=r.UInt(4),ticks=r.UInt(4);if(scale>0 && ticks!=uint.MaxValue)duration=(double)ticks/scale;}
                    else if(version==1 && next-r.Stream.Position>=28){r.UInt(8);r.UInt(8);ulong scale=r.UInt(4),ticks=r.UInt(8);if(scale>0 && ticks!=ulong.MaxValue)duration=(double)ticks/scale;}
                }
                r.Stream.Position=next;
            }
            return !fragmented && DurationMatching.Known(duration)?duration:0;
        }
        static double Matroska(HeaderReader r)
        {
            long end=r.Stream.Length;
            if(r.Vint(true,out _)!=0x1a45dfa3)return 0;
            ulong header=r.Vint(false,out bool unknown);if(unknown)return 0;
            r.Stream.Position=r.End(header,end);
            while(r.Stream.Position<end)
            {
                r.Check();ulong id=r.Vint(true,out _),size=r.Vint(false,out unknown);
                if(unknown && id!=0x18538067)return 0;
                long next=unknown?end:r.End(size,end);
                if(id==0x18538067)return Segment(r,next);
                r.Stream.Position=next;
            }
            return 0;
        }
        static double Segment(HeaderReader r,long end)
        {
            while(r.Stream.Position<end)
            {
                r.Check();ulong id=r.Vint(true,out _),size=r.Vint(false,out bool unknown);
                if(unknown)return 0;
                long next=r.End(size,end);
                if(id==0x1549a966)
                {
                    double ticks=0;ulong scale=1000000;
                    while(r.Stream.Position<next)
                    {
                        r.Check();ulong field=r.Vint(true,out _),length=r.Vint(false,out unknown);
                        if(unknown)return 0;
                        long fieldEnd=r.End(length,next);
                        if(field==0x2ad7b1 && length>0 && length<=8)scale=r.UInt((int)length);
                        else if(field==0x4489)
                        {
                            if(length==8)ticks=BitConverter.Int64BitsToDouble(unchecked((long)r.UInt(8)));
                            else if(length==4)ticks=BitConverter.Int32BitsToSingle(unchecked((int)r.UInt(4)));
                        }
                        r.Stream.Position=fieldEnd;
                    }
                    double duration=ticks*scale/1000000000.0;
                    return DurationMatching.Known(duration)?duration:0;
                }
                r.Stream.Position=next;
            }
            return 0;
        }
    }
}
