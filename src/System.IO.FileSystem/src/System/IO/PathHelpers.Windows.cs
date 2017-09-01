// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Diagnostics;

namespace System.IO
{
    internal static partial class PathHelpers
    {
        // Trim trailing whitespace, tabs etc but don't be aggressive in removing everything that has UnicodeCategory of trailing space.
        // string.WhitespaceChars will trim more aggressively than what the underlying FS does (for ex, NTFS, FAT).    
        internal static readonly char[] TrimEndChars = { (char)0x9, (char)0xA, (char)0xB, (char)0xC, (char)0xD, (char)0x20, (char)0x85, (char)0xA0 };
        internal static readonly char[] TrimStartChars = { ' ' };

        internal static bool ShouldReviseDirectoryPathToCurrent(string path)
        {
            // In situations where this method is invoked, "<DriveLetter>:" should be special-cased 
            // to instead go to the current directory.
            return path.Length == 2 && path[1] == ':';
        }

        // ".." can only be used if it is specified as a part of a valid File/Directory name. We disallow
        //  the user being able to use it to move up directories. Here are some examples eg 
        //    Valid: a..b  abc..d
        //    Invalid: ..ab   ab..  ..   abc..d\abc..
        //
        internal static void CheckSearchPattern(string searchPattern)
        {
            for (int index = 0; (index = searchPattern.IndexOf("..", index, StringComparison.Ordinal)) != -1; index += 2)
            {
                // Terminal ".." or "..\". File and directory names cannot end in "..".
                if (index + 2 == searchPattern.Length || 
                    PathInternal.IsDirectorySeparator(searchPattern[index + 2]))
                {
                    throw new ArgumentException(SR.Arg_InvalidSearchPattern, nameof(searchPattern));
                }
            }
        }

        // this is a lightweight version of GetDirectoryName that doesn't renormalize
        internal static string GetDirectoryNameInternal(string path)
        {
            string directory, file;
            SplitDirectoryFile(path, out directory, out file);

            // file is null when we reach the root
            return (file == null) ? null : directory;
        }

        internal static void SplitDirectoryFile(string path, out string directory, out string file)
        {
            directory = null;
            file = null;

            // assumes a validated full path
            if (path != null)
            {
                int length = path.Length;
                int rootLength = PathInternal.GetRootLength(path);

                // ignore a trailing slash
                if (length > rootLength && EndsInDirectorySeparator(path))
                    length--;

                // find the pivot index between end of string and root
                for (int pivot = length - 1; pivot >= rootLength; pivot--)
                {
                    if (PathInternal.IsDirectorySeparator(path[pivot]))
                    {
                        directory = path.Substring(0, pivot);
                        file = path.Substring(pivot + 1, length - pivot - 1);
                        return;
                    }
                }

                // no pivot, return just the trimmed directory
                directory = path.Substring(0, length);
            }
        }

        internal static string NormalizeSearchPattern(string searchPattern)
        {
            Debug.Assert(searchPattern != null);

            // Win32 normalization trims only U+0020.
            string tempSearchPattern = searchPattern.TrimEnd(PathHelpers.TrimEndChars);

            // Make this corner case more useful, like dir
            if (tempSearchPattern.Equals("."))
            {
                tempSearchPattern = "*";
            }

            CheckSearchPattern(tempSearchPattern);
            return tempSearchPattern;
        }

        internal static string GetFullSearchString(string fullPath, string searchPattern)
        {
            Debug.Assert(fullPath != null);
            Debug.Assert(searchPattern != null);

            ThrowIfEmptyOrRootedPath(searchPattern);
            string tempStr = Path.Combine(fullPath, searchPattern);

            // If path ends in a trailing slash (\), append a * or we'll get a "Cannot find the file specified" exception
            char lastChar = tempStr[tempStr.Length - 1];
            if (PathInternal.IsDirectorySeparator(lastChar) || lastChar == Path.VolumeSeparatorChar)
            {
                tempStr = tempStr + "*";
            }

            return tempStr;
        }

        internal static string TrimEndingDirectorySeparator(string path) =>
            EndsInDirectorySeparator(path) ?
                path.Substring(0, path.Length - 1) :
                path;




        /// <summary>
        /// 
        /// </summary>
        /// <param name="path"></param>
        /// <param name="allowTrailingSeparator"></param>
        /// <returns></returns>
        internal unsafe static PooledCharBuffer FastNormalizePath(string path, bool allowTrailingSeparator = false)
        {
            PooledCharBuffer pooledBuffer = new PooledCharBuffer();

            // Nulls are never valid in paths and result in unexpected behavior when calling Win32 APIs
            // as they mark the end of the string
            if (path.IndexOf('\0') != -1)
                return pooledBuffer;

            // Normalizing doesn't make sense for \\?\ paths
            if (PathInternal.IsExtended(path))
            {
                // TODO: Can we just always ignore these? E.g. allow a breaking change
                if (path.Length > PathInternal.ExtendedPathPrefix.Length
                    && path[path.Length - 1] == '\\' && path[path.Length -2] != ':')
                {
                    if (allowTrailingSeparator)
                    {
                        char[] charPath = pooledBuffer.Rent(path.Length);
                        path.CopyTo(0, charPath, 0, path.Length - 1);
                        charPath[path.Length - 1] = '\0';
                        pooledBuffer.SetSlice(0, path.Length - 1);
                    }

                    return pooledBuffer;
                }

                pooledBuffer.SetString(path);
                return pooledBuffer;
            }

            // TODO: Try with and without initial call for length (perf)
            uint result = 260;
            //uint result = Interop.Kernel32.GetFullPathNameW(path, 0, null, IntPtr.Zero);
            //if (result == 0)
            //    return pooledBuffer;

            const int CharsToReserve = 6;

            char[] buffer = null;
            do
            {
                buffer = pooledBuffer.Rent((int)result + CharsToReserve);
                fixed (char* c = buffer)
                {
                    result = Interop.Kernel32.GetFullPathNameW(path, (uint)buffer.Length - CharsToReserve, c + CharsToReserve, IntPtr.Zero);
                }

                if (result == 0)
                {
                    pooledBuffer.SetSlice(0, 0);
                    return pooledBuffer;
                }
            } while (result > buffer.Length);

            if (buffer[CharsToReserve + result - 1] == '\\')
            {
                if (allowTrailingSeparator)
                {
                    // C:\ is a special case, we can't remove the trailing slash in \\?\ format.
                    // There is no valid path that can come back at 3 characters other than C:\
                    if (result != 3)
                    {
                        buffer[CharsToReserve + --result] = '\0';
                    }
                }
                else
                {
                    pooledBuffer.SetSlice(0, 0);
                    return pooledBuffer;
                }
            }

#if NO_PREFIX
            pooledBuffer.SetSlice(CharsToReserve, (int)result);
            return pooledBuffer;
#else 
            if (buffer.Length > CharsToReserve + 2 && buffer[CharsToReserve] == '\\' && buffer[CharsToReserve + 1] == '\\')
            {
                if (buffer[CharsToReserve + 2] == '.')
                {
                    // This is \\. convert to \\?
                    buffer[CharsToReserve + 2] = '?';
                    pooledBuffer.SetSlice(6, (int)result);
                    return pooledBuffer;
                }
                else if (buffer[CharsToReserve + 2] != '\\')
                {
                    // UNC convert to \\
                    buffer[0] = '\\';
                    buffer[1] = '\\';
                    buffer[2] = '?';
                    buffer[3] = '\\';
                    buffer[4] = 'U';
                    buffer[5] = 'N';
                    buffer[6] = 'C';
                    pooledBuffer.SetSlice(0, (int)result + 6);
                    return pooledBuffer;
                }
            }

            buffer[2] = '\\';
            buffer[3] = '\\';
            buffer[4] = '?';
            buffer[5] = '\\';
            pooledBuffer.SetSlice(2, (int)result + 4);
            return pooledBuffer;
#endif
        }
    }

    internal struct PooledCharBuffer : IDisposable
    {
        public char[] _buffer;
        private int _start;
        private int _length;
        private string _string;

        public char[] Rent(int minimumLength)
        {
            if (_buffer != null)
                ArrayPool<char>.Shared.Return(_buffer);
            return _buffer = ArrayPool<char>.Shared.Rent(minimumLength);
        }

        public void SetSlice(int start)
        {
            Debug.Assert(_buffer != null, "should have a buffer");
            _start = start;
            _length = _buffer.Length;
        }

        public void SetSlice(int start, int length)
        {
            Debug.Assert(_buffer != null, "should have a buffer");
            _start = start;
            _length = length;
        }

        public void SetString(string value)
        {
            Debug.Assert(_buffer == null, "should NOT have a buffer");
            _string = value;
            _length = value.Length;
        }

        public void Dispose()
        {
            if (_buffer != null)
                ArrayPool<char>.Shared.Return(_buffer);
            _buffer = null;
            _length = 0;
        }

        public ReadOnlySpan<char> Span
        {
            get
            {
                Debug.Assert(_buffer == null || _string == null, "shouldn't have a char buffer and a string");
                return _string != null
                    ? _string.AsReadOnlySpan()
                    : new ReadOnlySpan<char>(_buffer, _start, _length);
            }
        }
 
        public bool IsEmpty => _length == 0;
    }

    //internal struct PooledBufferSpan : IDisposable
    //{
    //    public ReadOnlySpan<char> Span;
    //    public char[] Buffer;

    //    public PooledBufferSpan(char[] buffer)
    //    {
    //        Span = buffer == null ? ReadOnlySpan<char>.Empty : new ReadOnlySpan<char>(buffer);
    //        Buffer = buffer;
    //    }

    //    public PooledBufferSpan(char[] buffer, int start)
    //    {
    //        Span = new ReadOnlySpan<char>(buffer, start);
    //        Buffer = buffer;
    //    }

    //    public PooledBufferSpan(char[] buffer, int start, int length)
    //    {
    //        Span = new ReadOnlySpan<char>(buffer, start, length);
    //        Buffer = buffer;
    //    }

    //    public PooledBufferSpan(string buffer)
    //    {
    //        Span = buffer.ToCharArray();
    //        Buffer = null;
    //    }

    //    public static PooledBufferSpan Empty = new PooledBufferSpan((char[])null);

    //    public void Dispose()
    //    {
    //        if (Buffer != null)
    //            ArrayPool<char>.Shared.Return(Buffer);
    //    }

    //    public bool IsEmpty => Span.Length == 0;
    //}
}
