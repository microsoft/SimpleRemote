# Copyright (c) Microsoft. All rights reserved.
# Licensed under the MIT license. See LICENSE file in the project root for full license information.


# build SimpleRemote and all associated docs

# CONSTANTS
$BASEDIR = $PSScriptRoot
$OUTDIR = "$BASEDIR/output"
$INNOINSTALL = "C:\Program Files (x86)\Inno Setup 5\ISCC.exe"

# useful other variables
$VER_SUFFIX = (Get-Date -Format FileDate)

# clean output directory and projects
if (Test-Path $OUTDIR)
{
    rm -r $OUTDIR
}
mkdir $OUTDIR | out-null
dotnet clean | out-null

# build the common library package
echo "Building common library package..."
cd $BASEDIR\SimpleDUTCommonLibrary
dotnet pack -o $OUTDIR/nuget -c Release | Out-Null

# build the client
echo "Building client library..."
cd $BASEDIR\SimpleDUTClientLibrary
dotnet publish -o $OUTDIR/SimpleRemoteClient-x64 --version-suffix $VER_SUFFIX -f net46 -r win10-x64 -c Release | Out-Null
dotnet publish -o $OUTDIR/SimpleRemoteClient-x86 --version-suffix $VER_SUFFIX -f net46 -r win10-x86 -c Release | Out-Null
dotnet pack -o $OUTDIR/nuget -c Release | Out-Null


# build the rpc server nuget binary
echo "Building RPC server package"
cd $BASEDIR\SimpleJsonRpc
dotnet pack -o $OUTDIR/nuget -c Release | Out-Null

# build the server
echo "Building server..."
cd $BASEDIR\SimpleRemoteConsole
dotnet publish -o $OUTDIR/SimpleRemoteServer-x64 -f net46 -r win10-x64 -c Release | Out-Null
dotnet publish -o $OUTDIR/SimpleRemoteServer-arm64 -f netcoreapp2.0 -r win10-arm64 -c Release | Out-Null
dotnet publish -o $OUTDIR/SimpleRemoteServer-x64-WCOS -f netcoreapp2.0 -r win10-x64 -c Release | Out-Null

# build docs
if (gcm doxygen -ErrorAction SilentlyContinue)
{
    echo "Building docs..."
    cd $BASEDIR
    doxygen 2>&1 | Out-Null
    cp -r .\doc\html $OUTDIR/htmldoc
}
else {
    echo "Doxygen not found, skipping doc build."
}

# build the installer
if (Test-Path $INNOINSTALL)
{
    echo "Building installer..."
    cd $BASEDIR
    & $INNOINSTALL /q installer.iss
}
else {
    echo "Inno install not found - skipping installer generation."
}