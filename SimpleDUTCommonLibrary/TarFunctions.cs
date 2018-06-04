// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using ICSharpCode.SharpZipLib.Tar;

namespace SimpleDUTCommonLibrary
{
    public class TarFunctions
    {
        public static long WriteFileOrDirectoryToStream(Stream stream, string targetPath, bool closeStreamWhenComplete = true)
        {
            long bytesSent = 0;
            TarArchive tar = TarArchive.CreateOutputTarArchive(stream);
            tar.IsStreamOwner = closeStreamWhenComplete;
            string[] fileList = null;
            string rootPath = null;

            // handle globs, directories, and individual files.
            if (targetPath.Contains("*") || targetPath.Contains("?"))
            {
                // we're handling a glob
                rootPath = Path.GetDirectoryName(targetPath);
                fileList = GlobFunctions.Glob(targetPath);

            }
            else if (File.GetAttributes(targetPath).HasFlag(FileAttributes.Directory))
            {
                // handling a directory
                rootPath = targetPath;
                fileList = Directory.GetFileSystemEntries(targetPath, "*", SearchOption.AllDirectories);
            }
            else
            {
                // handling a single file
                rootPath = Path.GetDirectoryName(targetPath);
                fileList = new string[] { targetPath };
            }

            // see SharpZipLib GitHub Wiki for details. Short version is the root
            // path must use forward slashes, and not end with a slash.
            tar.RootPath = rootPath.Replace('\\', '/');
            if (tar.RootPath.EndsWith("/"))
            {
                // remove the trailing slash
                tar.RootPath = tar.RootPath.Remove(tar.RootPath.Length - 1);
            }
           

            foreach (var entry in fileList)
            {
                var tarEntry = TarEntry.CreateEntryFromFile(entry);
                tar.WriteEntry(tarEntry, false);

                // if it's a file, count it's size
                if (!File.GetAttributes(entry).HasFlag(FileAttributes.Directory))
                    bytesSent += (new FileInfo(entry)).Length;
            }

            // close the archive
            tar.Close();

            // return our byte count
            return bytesSent;

        }

        public static long ReadFileOrDirectoryFromStream(Stream stream, string pathToWrite, 
            bool overwrite = true, bool closeStreamWhenComplete = true)
        {
            var tar = new TarInputStream(stream);
            tar.IsStreamOwner = closeStreamWhenComplete;
            long bytesRead = 0;

            // we can't use the simple ExtractContents because we need to be overwrite aware,
            // so we iterate instead
            TarEntry entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                var extractPath = Path.Combine(pathToWrite, entry.Name);

                if (entry.IsDirectory)
                {
                    // we don't have to worry about writing over directories
                    Directory.CreateDirectory(extractPath);
                }
                else
                {
                    // if overwrite is on, use Create FileMode. If not, use CreateNew, which will throw
                    // an IO error if the file already exists.
                    using (var fs = new FileStream(extractPath, overwrite ? FileMode.Create : FileMode.CreateNew))
                    {
                        tar.CopyEntryContents(fs);
                        bytesRead += entry.Size;
                    }
                }

            }
            tar.Close();

            return bytesRead;
        }
    }
}
