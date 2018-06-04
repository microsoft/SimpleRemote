// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

namespace SimpleDUTRemote
{
    internal class ReadWriteChecks
    {
        /// <summary>
        /// Check if the given path exists, and can be read. Works for files and directories.
        /// </summary>
        /// <param name="path">Path to check.</param>
        /// <returns>True if successful, false otherwise.</returns>
        internal static bool CheckReadFromFileOrDir(string path)
        {
            try
            {
                // handle glob expressions - if we see wildcards, just grab the parent directory
                if (path.Contains("*") || path.Contains("?"))
                {
                    path = Path.GetDirectoryName(path);
                }

                if (IsDir(path))
                {
                    // try to list contents, will throw if does not exist
                    var tmp = Directory.GetFileSystemEntries(path);

                    // if this didn't throw, we assume we're safe.
                    return true;
                }
                else
                {
                    // this is a file, can we open it for reading?
                    using (var fstream = File.Open(path, FileMode.Open))
                    {
                        // if we're here, we can open the file without issue.
                        return true;
                    }
                }
            }
            catch (IOException)
            {
                return false;
            }

        }

        /// <summary>
        /// Check if the given path exists, and can be written. Works for directories only.
        /// </summary>
        /// <param name="path">Path to directory to check</param>
        /// <returns></returns>
        internal static bool CheckWriteToDir(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    throw new FileNotFoundException($"Target directory {path} doesn't exist.");
                }

                // can we write a file here?
                using (var fstream = File.Create(
                    Path.Combine(path, Path.GetRandomFileName()),
                    1024,
                    FileOptions.DeleteOnClose))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                return false;
            }

        }

        private static bool IsDir(string path)
        {
            // check if path is a directory. 
            // should also throw if path doesn't exist
            var fattrs = File.GetAttributes(path);
            return fattrs.HasFlag(FileAttributes.Directory);
        }

    }
}
