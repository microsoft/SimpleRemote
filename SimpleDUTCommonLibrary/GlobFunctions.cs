// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

namespace SimpleDUTCommonLibrary
{
    public class GlobFunctions
    {
        public static string[] Glob(string path)
        {
            if (!(path.Contains("*") || path.Contains("?")))
            {
                return new string[] { path }; //not a glob expression
            }

            // handle glob expression
            var parentDir = Path.GetDirectoryName(path);
            var globExp = Path.GetFileName(path);

            List<string> entries = new List<string>();
            entries.AddRange(Directory.GetFiles(parentDir, globExp)); // add files that meet the glob expression

            // we now need to figure out what folders met the glob expression, and if so, add their elements recursively
            var folders = Directory.GetDirectories(parentDir, globExp);

            foreach (var dir in folders)
            {
                entries.Add(dir);
                entries.AddRange(Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories));
            }

            return entries.ToArray();
        }
    }
}
