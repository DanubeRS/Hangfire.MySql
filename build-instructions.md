#Building Release Package#

MSBuild.exe .\build.proj /t:Release /p:BuildVersion=#.#.#

This will create a set of packages that can be found in build/bin. The semver scheme is in use for this project.

BuildLastMajorVersion is changed to the last MAJOR build number, as to keep dependants pinned back for breaking releases.
If a custom build is required, use the /p:CustomBuildName property.
