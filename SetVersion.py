# Copyright (c) Microsoft. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.

"""Set the version in all csproj and inno setup files."""

import os, os.path, re, sys

# extensions to process
EXTENSIONS = [".csproj", ".inno"]

def main():
    targetVer = sys.argv[1]
    fileList = GetFilesToModify()

    ModifyCsprojFiles(fileList, targetVer)
    ModifyIssFiles(fileList, targetVer)

    print "Done."



def GetFilesToModify():
    fileList = []
    for path, dirs, files in os.walk("."):
        f = []
        for ext in EXTENSIONS:
            f.extend([x for x in files if os.path.splitext(x)[-1] == ext])
        fileList.extend([os.path.join(path, x) for x in f])
    return fileList

def ModifyCsprojFiles(fileList, targetVersion):
    csprojFiles = [f for f in fileList if os.path.splitext(f)[-1] == ".csproj"]
    for csproj in csprojFiles:
        print "Updating file ", csproj
        with open(csproj, "rb+") as f:
            fileData = f.read()
            f.seek(0)
            fileData = re.sub(r"<Version>(.*?)</Version>", 
                "<Version>{0}</Version>".format(targetVersion), fileData)
            fileData = re.sub(r"<VersionPrefix>(.*?)</VersionPrefix>", 
                "<VersionPrefix>{0}</VersionPrefix>".format(_GetVersionPrefix(targetVersion)), fileData)
            f.write(fileData)

def ModifyIssFiles(fileList, targetVersion):
    issFiles = [f for f in fileList if os.path.splitext(f)[-1] == ".iss"]
    for issFile in issFiles:
        print "Updating file ", issFile
        with open(issFile, "rb+") as f:
            fileData = f.read()
            f.seek(0)
            fileData = re.sub(r"(#define MyAppVersion ).*", 
                r'\1 "{0}"'.format(targetVersion),
                fileData)
            f.write(fileData)


def _GetVersionPrefix(targetVersion):
    """Assuming semver, truncate the last number"""
    truncVer = targetVersion.split(".")[:-1]
    return ".".join(truncVer)

if __name__ == "__main__":
    main()