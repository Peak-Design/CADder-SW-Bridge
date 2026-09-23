<#
.SYNOPSIS
The checks Make-Release.ps1 runs before it builds anything.

.DESCRIPTION
Make-Release.ps1 dot-sources this file. The checks are in a file of their
own so that the tests can run them on a scratch git repository.
#>

function Test-ReleaseVersion {
    <#
    .SYNOPSIS
    Returns why a build of this version must not go ahead, or $null.

    .DESCRIPTION
    A build deletes and writes dist\CADder-Bridge-<version>, its zip and its
    setup.exe. When the version is already released (tag sw-v<version>),
    only the tagged commit, with no changes on top, may build it again.
    Before this check, a test build of a later commit overwrote the released
    files with a build that called itself the same version.
    #>
    param(
        [Parameter(Mandatory)] [string]$Repo,
        [Parameter(Mandatory)] [string]$Version,
        [switch]$Tag,
        [switch]$Dirty
    )
    $tagName = "sw-v$Version"
    $exists = git -C $Repo tag --list $tagName
    if (-not $exists) { return $null }
    if ($Tag) {
        return "tag $tagName exists. Bump <Version> in the csproj first."
    }
    $released = git -C $Repo rev-list -n 1 $tagName
    $head = git -C $Repo rev-parse HEAD
    if ($released -ne $head -or $Dirty) {
        return "version $Version is released as $tagName, and this tree is not that " +
            "release. A build now would overwrite the released files in dist. " +
            "Bump <Version> in the csproj first."
    }
    return $null
}
