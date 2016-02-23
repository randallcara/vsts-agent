# VSTS Cross Platform Agent (CoreCLR)

## Overview

A cross platform build and release agent for Visual Studio Team Services and Team Foundation Server 2015 and beyond.

Supported on Windows, OSX and Linux.  Written for the .NET Core CLR as one code base in C#.

## Install

Not available yet.  Need to build from source.  See Contribute below.

## Contribute (Dev)

### Dev Dependencies

![Win](docs/win_sm.png)![*nix](docs/linux_sm.png) .NET Core [Install Here](https://dotnet.github.io/getting-started/)  

![Win](docs/win_sm.png) Git for Windows [Install Here](https://git-scm.com/downloads) _(needed for dev sh script)

### Build, Test, Clean, Restore 

From src:

![Win](docs/win_sm.png) `dev {command}`  

![*nix](docs/linux_sm.png) `./dev.sh {command}`
  
** Commands: **

`restore` (`r`): Run first time and any time you change a project.json  

`build` (`b`):   build everything  

`test` (`t`):    run unit tests
        
  results in: 
  Test/bin/Debug/dnxcore50/{platform}/testResults.xml

`buildtest` (`bt`): build and test

`clean` (`c`):   deletes build output for each projects
 
`layout` (`l`): Creates a full layout in {root}/_layout  
   Does a clean, restore, build, publish and copy

`update` (`u`) {dirname}: Builds and publishes just one dir.  Patches the layout
   update {dirname}
   Use if you change code in an assembly and don't want to wait for the full layout.

`validate` (`v`): Precheckin validation.  Runs git clean, layout and test.

### Editors

[Using Visual Studio 2015](docs/dev/vs.md)  
[Using Visual Studio Code and Mono Debugger](docs/dev/code.md)  

# Styling convention
## Member organization
Constructors, visibility (public -> private), properties, methods (abstract first), static at the bottom, fields at the bottom.
