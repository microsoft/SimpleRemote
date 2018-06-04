// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SimpleDUTCommonLibrary
{
    public static class ZipFunctions
    {
        public static long WriteDirectoryToStream(Stream stream, string pathToDirectory)
        {
            using (PositionWrapperStream wrappedStream = new PositionWrapperStream(stream))
            using (ZipArchive zip = new ZipArchive(wrappedStream, ZipArchiveMode.Create))
            {
                // track how much we've sent (ignoring zip overhead)
                long bytesSent = 0;

                // use URIs to calculate relative paths, as discussed in 
                // https://stackoverflow.com/questions/9042861/how-to-make-an-absolute-path-relative-to-a-particular-folder
                Uri relativeRoot = new Uri(pathToDirectory.Last() == '\\' ? pathToDirectory : pathToDirectory + '\\', UriKind.Absolute);
                
                // get all entries in the path
                var entries = (new DirectoryInfo(pathToDirectory)).EnumerateFileSystemInfos("*", SearchOption.AllDirectories);

                // add entries to the zip file
                foreach (var entry in entries)
                {
                    string entryName = relativeRoot.MakeRelativeUri(new Uri(entry.FullName, UriKind.Absolute)).ToString();

                    // if we're handling a directory, add a blank entry
                    if (entry.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        // calculate the relative path as described here: 
                        zip.CreateEntry(entryName + "/");
                    }

                    // we're handling a file
                    else
                    {
                        zip.CreateEntryFromFile(entry.FullName, entryName, CompressionLevel.NoCompression);
                        bytesSent += (new FileInfo(entry.FullName)).Length;
                    }
                }

                // return the number of bytes sent
                return bytesSent;

            }
        }

        public static long ReadDirectoryFromStream(Stream stream, string pathToWrite, bool overwrite = true)
        {
            using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                Directory.CreateDirectory(pathToWrite);
                string targetPath;
                long bytesReceived = 0;

                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    targetPath = Path.Combine(pathToWrite, entry.FullName);

                    if (entry.FullName.EndsWith("/"))
                    {
                        // we're handling a directory entry
                        // this is always safe, regardless if overwrite is on or not.
                        Directory.CreateDirectory(targetPath);
                    }
                    else
                    {
                        // handle files
                        entry.ExtractToFile(targetPath, overwrite);
                        bytesReceived += entry.Length;
                    }
                }

                return bytesReceived;
            }
        }

        /// <summary>
        /// Helper Class For Network Streams with Position Tracking 
        /// </summary>
        /// <remarks>
        /// This class exists due to a bug in the .NET Framework where ZipArchives
        /// try to access position of a stream, even in create mode.
        /// <br/>Code stolen from: https://stackoverflow.com/questions/16585488/writing-to-ziparchive-using-the-httpcontext-outputstream
        /// <br/>Bug: https://connect.microsoft.com/VisualStudio/feedback/details/816411/ziparchive-shouldnt-read-the-position-of-non-seekable-streams
        /// <br/>
        /// <br/>This is fixed in .NET Core 2.0: https://github.com/dotnet/corefx/pull/12682
        /// </remarks>
        private class PositionWrapperStream : Stream
        {
            private readonly Stream wrapped;

            private int pos = 0;

            public PositionWrapperStream(Stream wrapped)
            {
                this.wrapped = wrapped;
            }

            public override bool CanSeek { get { return false; } }

            public override bool CanWrite { get { return true; } }

            public override long Position
            {
                get { return pos; }
                set { throw new NotSupportedException(); }
            }

            public override bool CanRead => throw new NotImplementedException();

            public override long Length => throw new NotImplementedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                pos += count;
                wrapped.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                wrapped.Flush();
            }

            protected override void Dispose(bool disposing)
            {
                wrapped.Dispose();
                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotImplementedException();
            }

            public override void SetLength(long value)
            {
                throw new NotImplementedException();
            }

            // all the other required methods can throw NotSupportedException

        }
    }


}
