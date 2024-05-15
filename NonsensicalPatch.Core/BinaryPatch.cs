using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.GZip;
using System.Buffers.Binary;

namespace NonsensicalPatch.Core;

/*
The original bsdiff.c source code (http://www.daemonology.net/bsdiff/) is
distributed under the following license:

Copyright 2003-2005 Colin Percival
All rights reserved

Redistribution and use in source and binary forms, with or without
modification, are permitted providing that the following conditions
are met:
1. Redistributions of source code must retain the above copyright
	notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright
	notice, this list of conditions and the following disclaimer in the
	documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE AUTHOR ``AS IS'' AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED.  IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS
OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING
IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
*/

//来源：https://github.com/LogosBible/bsdiff.net 略有修改
public static class BinaryPatch
{
    private const long c_fileSignature = 0x3034464649445342L;
    private const int c_headerSize = 32;

    /// <summary>
    /// Creates a binary patch (in <a href="https://www.daemonology.net/bsdiff/">bsdiff</a> format) that can be used
    /// (by <see cref="Apply"/>) to transform <paramref name="oldData"/> into <paramref name="newData"/>.
    /// </summary>
    /// <param name="oldData">The original binary data.</param>
    /// <param name="newData">The new binary data.</param>
    /// <param name="output">A <see cref="Stream"/> to which the patch will be written.</param>
    public static void Create(ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, Stream output, CompressType compressType)
    {
        // check arguments
        if (output is null)
            throw new ArgumentNullException(nameof(output));
        if (!output.CanSeek)
            throw new ArgumentException("Output stream must be seekable.", nameof(output));
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable.", nameof(output));

        /* Header is
			0	8	 "BSDIFF40"
			8	8	X length of bzip2ed ctrl block
			16	8	Y length of bzip2ed diff block
			24	8	length of new file */
        /* File is
			0	32	Header
			32	X	Bzip2ed ctrl block
		   32+X	Y	Bzip2ed diff block
		 32+X+Y	??	Bzip2ed extra block */
        Span<byte> header = stackalloc byte[c_headerSize];
        WriteInt64(header, c_fileSignature); // "BSDIFF40"
        WriteInt64(header[24..], newData.Length);

        var startPosition = output.Position;
        output.Write(header);

        var I = SuffixSort(oldData);

        var db = new byte[newData.Length];
        var eb = new byte[newData.Length];

        var dblen = 0;
        var eblen = 0;
        byte[] buffer = new byte[8]; // use an actual byte array because BZip2OutputStream may not support the Span<byte> overload natively


        using (Stream zipStream = compressType == CompressType.Gzip ? new GZipOutputStream(output) { IsStreamOwner = false } : new BZip2OutputStream(output) { IsStreamOwner = false })
        {
            // compute the differences, writing ctrl as we go
            var scan = 0;
            var pos = 0;
            var len = 0;
            var lastscan = 0;
            var lastpos = 0;
            var lastoffset = 0;
            while (scan < newData.Length)
            {
                var oldscore = 0;

                for (var scsc = scan += len; scan < newData.Length; scan++)
                {
                    len = Search(I, oldData, newData, scan, 0, oldData.Length, out pos);

                    for (; scsc < scan + len; scsc++)
                    {
                        if (scsc + lastoffset < oldData.Length && oldData[scsc + lastoffset] == newData[scsc])
                            oldscore++;
                    }

                    if (len == oldscore && len != 0 || len > oldscore + 8)
                        break;

                    if (scan + lastoffset < oldData.Length && oldData[scan + lastoffset] == newData[scan])
                        oldscore--;
                }

                if (len != oldscore || scan == newData.Length)
                {
                    var s = 0;
                    var sf = 0;
                    var lenf = 0;
                    for (var i = 0; lastscan + i < scan && lastpos + i < oldData.Length;)
                    {
                        if (oldData[lastpos + i] == newData[lastscan + i])
                            s++;
                        i++;
                        if (s * 2 - i > sf * 2 - lenf)
                        {
                            sf = s;
                            lenf = i;
                        }
                    }

                    var lenb = 0;
                    if (scan < newData.Length)
                    {
                        s = 0;
                        var sb = 0;
                        for (var i = 1; scan >= lastscan + i && pos >= i; i++)
                        {
                            if (oldData[pos - i] == newData[scan - i])
                                s++;
                            if (s * 2 - i > sb * 2 - lenb)
                            {
                                sb = s;
                                lenb = i;
                            }
                        }
                    }

                    if (lastscan + lenf > scan - lenb)
                    {
                        var overlap = lastscan + lenf - (scan - lenb);
                        s = 0;
                        var ss = 0;
                        var lens = 0;
                        for (var i = 0; i < overlap; i++)
                        {
                            if (newData[lastscan + lenf - overlap + i] == oldData[lastpos + lenf - overlap + i])
                                s++;
                            if (newData[scan - lenb + i] == oldData[pos - lenb + i])
                                s--;
                            if (s > ss)
                            {
                                ss = s;
                                lens = i + 1;
                            }
                        }

                        lenf += lens - overlap;
                        lenb -= lens;
                    }

                    for (var i = 0; i < lenf; i++)
                        db[dblen + i] = (byte)(newData[lastscan + i] - oldData[lastpos + i]);
                    for (var i = 0; i < scan - lenb - (lastscan + lenf); i++)
                        eb[eblen + i] = newData[lastscan + lenf + i];

                    dblen += lenf;
                    eblen += scan - lenb - (lastscan + lenf);

                    WriteInt64(buffer, lenf);
                    zipStream.Write(buffer, 0, buffer.Length);

                    WriteInt64(buffer, scan - lenb - (lastscan + lenf));
                    zipStream.Write(buffer, 0, 8);

                    WriteInt64(buffer, pos - lenb - (lastpos + lenf));
                    zipStream.Write(buffer, 0, 8);

                    lastscan = scan - lenb;
                    lastpos = pos - lenb;
                    lastoffset = pos - scan;
                }
            }
        }

        // compute size of compressed ctrl data
        var controlEndPosition = output.Position;
        WriteInt64(header[8..], controlEndPosition - startPosition - c_headerSize);

        // write compressed diff data
        using (Stream zipStream = compressType == CompressType.Gzip ? new GZipOutputStream(output) { IsStreamOwner = false } : new BZip2OutputStream(output) { IsStreamOwner = false })
            zipStream.Write(db, 0, dblen);

        // compute size of compressed diff data
        long diffEndPosition = output.Position;
        WriteInt64(header[16..], diffEndPosition - controlEndPosition);

        // write compressed extra data
        using (Stream zipStream = compressType == CompressType.Gzip ? new GZipOutputStream(output) { IsStreamOwner = false } : new BZip2OutputStream(output) { IsStreamOwner = false })
            zipStream.Write(eb, 0, eblen);

        // seek to the beginning, write the header, then seek back to end
        long endPosition = output.Position;
        output.Position = startPosition;
        output.Write(header);
        output.Position = endPosition;
    }

    /// <summary>
    /// Applies a binary patch (in <a href="http://www.daemonology.net/bsdiff/">bsdiff</a> format) to the data in
    /// <paramref name="input"/> and writes the results of patching to <paramref name="output"/>.
    /// </summary>
    /// <param name="input">A <see cref="Stream"/> containing the input data.</param>
    /// <param name="openPatchStream">A func that can open a <see cref="Stream"/> positioned at the start of the patch data.
    /// This stream must support reading and seeking, and <paramref name="openPatchStream"/> must allow multiple streams on
    /// the patch to be opened concurrently.</param>
    /// <param name="output">A <see cref="Stream"/> to which the patched data is written.</param>
    public static void Apply(Stream input, Func<Stream> openPatchStream, Stream output, CompressType compressType)
    {
        // check arguments
        if (input is null)
            throw new ArgumentNullException(nameof(input));
        if (openPatchStream is null)
            throw new ArgumentNullException(nameof(openPatchStream));
        if (output is null)
            throw new ArgumentNullException(nameof(output));

        /*
		File format:
			0	8	"BSDIFF40"
			8	8	X
			16	8	Y
			24	8	sizeof(newfile)
			32	X	bzip2(control block)
			32+X	Y	bzip2(diff block)
			32+X+Y	???	bzip2(extra block)
		with control block a set of triples (x,y,z) meaning "add x bytes
		from oldfile to x bytes from the diff block; copy y bytes from the
		extra block; seek forwards in oldfile by z bytes".
		*/
        // read header
        long controlLength, diffLength, newSize;
        using (var patchStream = openPatchStream())
        {
            // check patch stream capabilities
            if (!patchStream.CanRead)
                throw new ArgumentException("Patch stream must be readable.", nameof(openPatchStream));
            if (!patchStream.CanSeek)
                throw new ArgumentException("Patch stream must be seekable.", nameof(openPatchStream));

            Span<byte> header = stackalloc byte[c_headerSize];
            patchStream.ReadExactly(header);

            // check for appropriate magic
            var signature = ReadInt64(header);
            if (signature != c_fileSignature)
                throw new InvalidOperationException("Corrupt patch.");

            // read lengths from header
            controlLength = ReadInt64(header[8..]);
            diffLength = ReadInt64(header[16..]);
            newSize = ReadInt64(header[24..]);
            if (controlLength < 0 || diffLength < 0 || newSize < 0)
                throw new InvalidOperationException("Corrupt patch.");
        }

        // preallocate buffers for reading and writing
        const int c_bufferSize = 1048576;
        var newData = new byte[c_bufferSize];
        var oldData = new byte[c_bufferSize];

        // prepare to read three parts of the patch in parallel
        using var compressedControlStream = openPatchStream();
        using var compressedDiffStream = openPatchStream();
        using var compressedExtraStream = openPatchStream();

        // seek to the start of each part
        compressedControlStream.Seek(c_headerSize, SeekOrigin.Current);
        compressedDiffStream.Seek(c_headerSize + controlLength, SeekOrigin.Current);
        compressedExtraStream.Seek(c_headerSize + controlLength + diffLength, SeekOrigin.Current);

        // decompress each part (to read it)
        using Stream controlStream = compressType == CompressType.Gzip ? new GZipInputStream(compressedControlStream) : new BZip2InputStream(compressedControlStream);
        using Stream diffStream = compressType == CompressType.Gzip ? new GZipInputStream(compressedDiffStream) : new BZip2InputStream(compressedDiffStream);
        using Stream extraStream = compressType == CompressType.Gzip ? new GZipInputStream(compressedExtraStream) : new BZip2InputStream(compressedExtraStream);
        Span<long> control = stackalloc long[3];
        Span<byte> buffer = stackalloc byte[8];

        var oldPosition = 0;
        var newPosition = 0;
        while (newPosition < newSize)
        {
            // read control data
            for (var i = 0; i < 3; i++)
            {
                controlStream.ReadExactly(buffer);
                control[i] = ReadInt64(buffer);
            }

            // sanity-check
            if (newPosition + control[0] > newSize)
                throw new InvalidOperationException("Corrupt patch.");

            // seek old file to the position that the new data is diffed against
            input.Position = oldPosition;

            var bytesToCopy = (int)control[0];
            while (bytesToCopy > 0)
            {
                var actualBytesToCopy = Math.Min(bytesToCopy, c_bufferSize);

                // read diff string
                diffStream.ReadExactly(newData, 0, actualBytesToCopy);

                // add old data to diff string
                var availableInputBytes = Math.Min(actualBytesToCopy, (int)(input.Length - input.Position));
                input.ReadExactly(oldData, 0, availableInputBytes);

                for (var index = 0; index < availableInputBytes; index++)
                    newData[index] += oldData[index];

                output.Write(newData, 0, actualBytesToCopy);

                // adjust counters
                newPosition += actualBytesToCopy;
                oldPosition += actualBytesToCopy;
                bytesToCopy -= actualBytesToCopy;
            }

            // sanity-check
            if (newPosition + control[1] > newSize)
                throw new InvalidOperationException("Corrupt patch.");

            // read extra string
            bytesToCopy = (int)control[1];
            while (bytesToCopy > 0)
            {
                var actualBytesToCopy = Math.Min(bytesToCopy, c_bufferSize);

                extraStream.ReadExactly(newData, 0, actualBytesToCopy);
                output.Write(newData, 0, actualBytesToCopy);

                newPosition += actualBytesToCopy;
                bytesToCopy -= actualBytesToCopy;
            }

            // adjust position
            oldPosition = (int)(oldPosition + control[2]);
        }
    }

    private static int CompareBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var length = Math.Min(left.Length, right.Length);
        return left[..length].SequenceCompareTo(right[..length]);
    }

    /// <summary>
    /// 相同前缀长度
    /// </summary>
    /// <param name="span"></param>
    /// <param name="other"></param>
    /// <returns></returns>
    private static int CommonPrefixLength(this ReadOnlySpan<byte> span, ReadOnlySpan<byte> other)
    {
        int index;
        for (index = 0; index < span.Length && index < other.Length; index++)
        {
            if (span[index] != other[index])
                break;
        }
        return index;
    }

    private static int Search(int[] I, ReadOnlySpan<byte> oldData, ReadOnlySpan<byte> newData, int newOffset, int start, int end, out int pos)
    {
        if (end - start < 2)
        {
            var startLength = oldData[I[start]..].CommonPrefixLength(newData[newOffset..]);
            var endLength = oldData[I[end]..].CommonPrefixLength(newData[newOffset..]);

            if (startLength > endLength)
            {
                pos = I[start];
                return startLength;
            }
            else
            {
                pos = I[end];
                return endLength;
            }
        }
        else
        {
            var midPoint = start + (end - start) / 2;
            return CompareBytes(oldData[I[midPoint]..], newData[newOffset..]) < 0 ?
                Search(I, oldData, newData, newOffset, midPoint, end, out pos) :
                Search(I, oldData, newData, newOffset, start, midPoint, out pos);
        }
    }

    private static void Split(int[] I, int[] v, int start, int len, int h)
    {
        Stack<int> starts = new Stack<int>();
        Stack<int> lens = new Stack<int>();
        Stack<TempValue?> temps = new Stack<TempValue?>();
        starts.Push(start);
        lens.Push(len);
        temps.Push(null);
        while (starts.Count > 0)
        {
            var crtStart = starts.Pop();
            var crtLen = lens.Pop();
            var crtTemp = temps.Pop();
            if (crtLen < 16)
            {
                int j;
                for (var k = crtStart; k < crtStart + crtLen; k += j)
                {
                    j = 1;
                    var x = v[I[k] + h];
                    for (var i = 1; k + i < crtStart + crtLen; i++)
                    {
                        if (v[I[k + i] + h] < x)
                        {
                            x = v[I[k + i] + h];
                            j = 0;
                        }
                        if (v[I[k + i] + h] == x)
                        {
                            Swap(ref I[k + j], ref I[k + i]);
                            j++;
                        }
                    }
                    for (var i = 0; i < j; i++)
                        v[I[k + i]] = k + j - 1;
                    if (j == 1)
                        I[k] = -1;
                }
            }
            else
            {
                if (crtTemp==null)
                {
                    var x = v[I[crtStart + crtLen / 2] + h];
                    var jj = 0;
                    var kk = 0;
                    for (var i2 = crtStart; i2 < crtStart + crtLen; i2++)
                    {
                        if (v[I[i2] + h] < x)
                            jj++;
                        if (v[I[i2] + h] == x)
                            kk++;
                    }
                    jj += crtStart;
                    kk += jj;

                    var i = crtStart;
                    var j = 0;
                    var k = 0;
                    while (i < jj)
                    {
                        if (v[I[i] + h] < x)
                        {
                            i++;
                        }
                        else if (v[I[i] + h] == x)
                        {
                            Swap(ref I[i], ref I[jj + j]);
                            j++;
                        }
                        else
                        {
                            Swap(ref I[i], ref I[kk + k]);
                            k++;
                        }
                    }

                    while (jj + j < kk)
                    {
                        if (v[I[jj + j] + h] == x)
                        {
                            j++;
                        }
                        else
                        {
                            Swap(ref I[jj + j], ref I[kk + k]);
                            k++;
                        }
                    }

                    if (jj > crtStart)
                    {
                        starts.Push(crtStart);
                        lens.Push(crtLen);
                        temps.Push(new TempValue(i,jj,kk)) ;


                        starts.Push(crtStart);
                        lens.Push(jj - crtStart);
                        temps.Push(null);
                        continue;
                    }
                    for (i = 0; i < kk - jj; i++)
                        v[I[jj + i]] = kk - 1;
                    if (jj == kk - 1)
                        I[jj] = -1;

                    if (crtStart + crtLen > kk)
                    {
                        starts.Push(kk);
                        lens.Push(crtStart + crtLen - kk);
                        temps.Push(null);
                    }
                }
                else
                {
                    for (crtTemp.i = 0; crtTemp.i < crtTemp.kk - crtTemp.jj; crtTemp.i++)
                        v[I[crtTemp.jj + crtTemp.i]] = crtTemp.kk - 1;
                    if (crtTemp.jj == crtTemp.kk - 1)
                        I[crtTemp.jj] = -1;
                    if (crtStart + crtLen > crtTemp.kk)
                    {
                        starts.Push(crtTemp.kk);
                        lens.Push(crtStart + crtLen - crtTemp.kk);
                        temps.Push(null);
                    }
                }
            }
        }

        static void Swap(ref int first, ref int second) => (second, first) = (first, second);
    }

    class TempValue
    {
        public int i;
        public int jj;
        public int kk;

        public TempValue(int i,  int jj, int kk)
        {
            this.i = i;
            this.jj = jj;
            this.kk = kk;
        }
    }
    
    /// <summary>
    /// 后缀排序
    /// </summary>
    /// <param name="oldData"></param>
    /// <returns></returns>
    private static int[] SuffixSort(ReadOnlySpan<byte> oldData)
    {
        Span<int> buckets = stackalloc int[256];

        //111144444522266007
        foreach (var oldByte in oldData)    //获取旧数据每一个字节的数量
            buckets[oldByte]++;
        //2  4  3  0  5  1  2  1
        for (var i = 1; i < 256; i++)       //先正序遍历加上前一位
            buckets[i] += buckets[i - 1];
        //2  6  9  9  14 15 17 18
        for (var i = 255; i > 0; i--)       //再全体后移一位
            buckets[i] = buckets[i - 1];
        buckets[0] = 0;
        //0  2  6  9  9  14 15 17

        //此时 buckets 中的值为初始赋值后每一位从0开始遍历到当前位之前的累和
        //其意义是确保 buckets[m]<buckets[n](m<n)，且在之后赋值过程中，每一个字节之间有足够空间

        //I[i] 表示将所有后缀排序后第 i 小的后缀的编号
        //v[i] 表示后缀数组中 i 的排名
        //I[v[i]]=i; v[I[i]]=i;
        //有效索引从1开始

        var I = new int[oldData.Length + 1];
        for (var i = 0; i < oldData.Length; i++)    //初始化子串长度为1时的I，通过自增防止相同的子串索引相同，同时还可以保证覆盖到I的每一个值
            I[++buckets[oldData[i]]] = i;
        //2  6  9  9  14 15 17 18

        var v = new int[oldData.Length + 1];
        for (var i = 0; i < oldData.Length; i++)    //初始化子串长度为1时的v，此时 buckets[oldData[i]] 已经自增过，所以存储的是相同字串中最靠后的排名
            v[i] = buckets[oldData[i]];


        for (var i = 1; i < 256; i++)               //如果buckets[i] == buckets[i - 1] + 1，则代表 (byte)i 在oldData中只出现过一次，。。。。。。
        {
            if (buckets[i] == buckets[i - 1] + 1)
                I[buckets[i]] = -1;
        }
        I[0] = -1;

        for (var h = 1; I[0] != -(oldData.Length + 1); h += h)
        {
            var len = 0;
            var i = 0;
            while (i < oldData.Length + 1)
            {
                if (I[i] < 0)
                {
                    len -= I[i];
                    i -= I[i];
                }
                else
                {
                    if (len != 0)
                        I[i - len] = -len;
                    len = v[I[i]] + 1 - i;
                    Split(I, v, i, len, h);
                    i += len;
                    len = 0;
                }
            }

            if (len != 0)
                I[i - len] = -len;
        }

        for (var i = 0; i < oldData.Length + 1; i++)
            I[v[i]] = i;

        return I;
    }

    // Reads a long value stored in sign/magnitude format.
    private static long ReadInt64(ReadOnlySpan<byte> buffer)
    {
        var value = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        var mask = value >> 63;
        return ~mask & value | (value & unchecked((long)0x8000_0000_0000_0000)) - value & mask;
    }

    // Writes a long value in sign/magnitude format.
    private static void WriteInt64(Span<byte> buffer, long value)
    {
        var mask = value >> 63;
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value + mask ^ mask | value & unchecked((long)0x8000_0000_0000_0000));
    }
}
