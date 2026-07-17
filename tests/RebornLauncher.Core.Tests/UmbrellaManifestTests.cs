using System.Text;
using RebornLauncher.Core.Models;
using RebornLauncher.Core.Services;

namespace RebornLauncher.Core.Tests;

public sealed class UmbrellaManifestTests
{
    private const string ValidVariant =
        """
        {
          "schemaVersion": 4,
          "id": "x64-dx9-vanilla",
          "project": "galaxies-reborn",
          "flavor": "nge",
          "variantPath": "galaxies-reborn/nge/x64-dx9-vanilla",
          "displayName": "Vanilla",
          "branch": "x64-dx9-vanilla",
          "pipeline": "swgSource",
          "server": {
            "mainRepositoryPath": "swg-main",
            "submodules": [{
              "name": "swg-main",
              "path": "galaxies-reborn/nge/x64-dx9-vanilla/swg-main",
              "installPath": "swg-main",
              "repository": "https://github.com/Galaxies-Reborn/swg-main",
              "branch": "x64-dx9-vanilla",
              "recursive": true,
              "required": true,
              "approximateBytes": 22523904
            }]
          },
          "clients": {
            "supported": true,
            "regularExecutable": "clients/regular.exe",
            "godExecutable": "clients/god.exe",
            "regularLoginConfiguration": "clients/login.cfg",
            "godLoginConfiguration": "clients/client.cfg"
          }
        }
        """;

    /// <summary>A project that offers variants directly, with no flavor level and no client payload.</summary>
    private const string FlavorlessVariant =
        """
        {
          "schemaVersion": 4,
          "id": "core3",
          "project": "swgemu",
          "flavor": "",
          "variantPath": "swgemu/core3",
          "displayName": "SWGEmu Core3",
          "branch": "unstable",
          "pipeline": "core3",
          "server": {
            "mainRepositoryPath": "Core3",
            "submodules": [{
              "name": "Core3",
              "path": "swgemu/core3/Core3",
              "installPath": "Core3",
              "repository": "https://github.com/swgemu/Core3",
              "branch": "unstable",
              "recursive": true,
              "required": true,
              "approximateBytes": 527802368
            }]
          },
          "clients": { "supported": false }
        }
        """;

    private static Task<ReleaseChannel> LoadAsync(string json) =>
        ChannelManifestService.LoadChannelAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task LoadChannelAcceptsSubmoduleManifest()
    {
        var channel = await LoadAsync(ValidVariant);

        Assert.Equal(4, channel.SchemaVersion);
        Assert.Equal("galaxies-reborn", channel.Project);
        Assert.Equal("nge", channel.Flavor);
        Assert.Equal("galaxies-reborn/nge/x64-dx9-vanilla", channel.VariantPath);
        Assert.Equal(PipelineKind.SwgSource, channel.Pipeline);
        Assert.True(channel.Clients.Supported);
        Assert.Single(channel.Server.Submodules);
    }

    [Fact]
    public async Task LoadChannelAcceptsAFlavorlessProject()
    {
        var channel = await LoadAsync(FlavorlessVariant);

        // A project without flavors nests one level shallower.
        Assert.Empty(channel.Flavor);
        Assert.Equal("swgemu/core3", channel.VariantPath);
        Assert.Equal(PipelineKind.Core3, channel.Pipeline);
        Assert.False(channel.Clients.Supported);
    }

    [Fact]
    public async Task LoadChannelRejectsAVariantPathThatContradictsItsIdentity()
    {
        var json = ValidVariant.Replace(
            "\"variantPath\": \"galaxies-reborn/nge/x64-dx9-vanilla\"",
            "\"variantPath\": \"swgemu/nge/x64-dx9-vanilla\"");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(json));
        Assert.Contains("galaxies-reborn/nge/x64-dx9-vanilla", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadChannelRejectsSubmodulePathOutsideItsVariant()
    {
        var json = ValidVariant.Replace(
            "\"path\": \"galaxies-reborn/nge/x64-dx9-vanilla/swg-main\"",
            "\"path\": \"swg-source/base/swg-main\"");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(json));
        Assert.Contains("galaxies-reborn/nge/x64-dx9-vanilla/swg-main", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadChannelRejectsTraversalInSubmodulePath()
    {
        var json = ValidVariant
            .Replace(
                "\"path\": \"galaxies-reborn/nge/x64-dx9-vanilla/swg-main\"",
                "\"path\": \"galaxies-reborn/nge/x64-dx9-vanilla/../../escape\"")
            .Replace("\"installPath\": \"swg-main\"", "\"installPath\": \"../escape\"");

        await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(json));
    }

    [Fact]
    public async Task LoadChannelRequiresMainRepositoryToBeRequired()
    {
        var json = ValidVariant.Replace("\"required\": true", "\"required\": false");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(json));
        Assert.Contains("swg-main", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadChannelRejectsASupportedClientWithoutPaths()
    {
        // Claiming a client while describing none would leave the launcher with nothing to launch.
        var json = FlavorlessVariant.Replace("\"supported\": false", "\"supported\": true");

        await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(json));
    }

    [Fact]
    public async Task LoadUmbrellaCatalogAcceptsProjects()
    {
        const string json =
            """
            {
              "schemaVersion": 4,
              "projects": [
                {
                  "id": "galaxies-reborn",
                  "displayName": "Galaxies Reborn",
                  "path": "galaxies-reborn",
                  "available": true,
                  "flavors": [
                    {
                      "id": "nge",
                      "displayName": "NGE",
                      "path": "galaxies-reborn/nge",
                      "available": true,
                      "variants": [{
                        "id": "x64-dx9-vanilla",
                        "displayName": "Vanilla",
                        "manifest": "channels/galaxies-reborn/nge/x64-dx9-vanilla.json"
                      }]
                    },
                    { "id": "cu", "displayName": "CU", "path": "galaxies-reborn/cu", "available": false, "variants": [] },
                    { "id": "pre-cu", "displayName": "Pre-CU", "path": "galaxies-reborn/pre-cu", "available": false, "variants": [] }
                  ],
                  "variants": []
                },
                {
                  "id": "swgemu",
                  "displayName": "SWGEmu",
                  "path": "swgemu",
                  "available": true,
                  "flavors": [],
                  "variants": [{
                    "id": "core3",
                    "displayName": "SWGEmu Core3",
                    "manifest": "channels/swgemu/core3.json"
                  }]
                }
              ]
            }
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var catalog = await ChannelManifestService.LoadUmbrellaCatalogAsync(stream);

        Assert.Equal(2, catalog.Projects.Count);
        var reborn = catalog.Projects[0];
        Assert.True(reborn.HasFlavors);
        Assert.Equal(3, reborn.Flavors.Count);

        var swgemu = catalog.Projects[1];
        Assert.False(swgemu.HasFlavors);
        Assert.Single(swgemu.Variants);

        // Both shapes are reachable through one accessor, so the launcher need not special-case them.
        Assert.Equal(2, catalog.AllVariants.Count());
    }

    [Fact]
    public async Task LoadUmbrellaCatalogAcceptsAnAnnouncedButUnpublishedVariant()
    {
        const string json =
            """
            {
              "schemaVersion": 4,
              "projects": [{
                "id": "galaxies-reborn",
                "displayName": "Galaxies Reborn",
                "path": "galaxies-reborn",
                "available": true,
                "flavors": [{
                  "id": "nge",
                  "displayName": "NGE",
                  "path": "galaxies-reborn/nge",
                  "available": true,
                  "variants": [
                    {
                      "id": "x64-dx9-vanilla", "displayName": "DX9", "renderer": "dx9",
                      "available": true, "manifest": "channels/galaxies-reborn/nge/x64-dx9-vanilla.json"
                    },
                    {
                      "id": "x64-dx11-vanilla", "displayName": "DX11", "renderer": "dx11",
                      "available": false, "manifest": ""
                    }
                  ]
                }],
                "variants": []
              }]
            }
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var catalog = await ChannelManifestService.LoadUmbrellaCatalogAsync(stream);

        var nge = catalog.Projects[0].Flavors[0];
        Assert.Equal([RendererKind.Dx9, RendererKind.Dx11], nge.Renderers);
        Assert.False(nge.Variants[1].Available);

        // An unpublished variant needs no manifest; the era stays available because DX9 is.
        Assert.Empty(nge.Variants[1].Manifest);
    }

    [Fact]
    public async Task LoadUmbrellaCatalogRejectsAnAvailableVariantWithoutAManifest()
    {
        const string json =
            """
            {
              "schemaVersion": 4,
              "projects": [{
                "id": "galaxies-reborn", "displayName": "Galaxies Reborn",
                "path": "galaxies-reborn", "available": true,
                "flavors": [],
                "variants": [{ "id": "x64-dx11-vanilla", "displayName": "DX11", "available": true, "manifest": "" }]
              }]
            }
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ChannelManifestService.LoadUmbrellaCatalogAsync(stream));
        Assert.Contains("no manifest", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadUmbrellaCatalogRejectsAnEraWhoseVariantsAreAllUnpublished()
    {
        const string json =
            """
            {
              "schemaVersion": 4,
              "projects": [{
                "id": "galaxies-reborn", "displayName": "Galaxies Reborn",
                "path": "galaxies-reborn", "available": true,
                "flavors": [{
                  "id": "cu", "displayName": "CU", "path": "galaxies-reborn/cu", "available": true,
                  "variants": [{ "id": "x64-dx11-cu", "displayName": "DX11", "available": false, "manifest": "" }]
                }],
                "variants": []
              }]
            }
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => ChannelManifestService.LoadUmbrellaCatalogAsync(stream));
    }

    [Fact]
    public async Task LoadUmbrellaCatalogRejectsAProjectDeclaringBothShapes()
    {
        const string json =
            """
            {
              "schemaVersion": 4,
              "projects": [{
                "id": "mixed",
                "displayName": "Mixed",
                "path": "mixed",
                "available": true,
                "flavors": [{
                  "id": "nge", "displayName": "NGE", "path": "mixed/nge", "available": true,
                  "variants": [{ "id": "a", "displayName": "A", "manifest": "channels/mixed/a.json" }]
                }],
                "variants": [{ "id": "b", "displayName": "B", "manifest": "channels/mixed/b.json" }]
              }]
            }
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ChannelManifestService.LoadUmbrellaCatalogAsync(stream));
        Assert.Contains("both flavors and direct variants", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadUmbrellaCatalogRejectsAvailableFlavorWithoutVariants()
    {
        const string json =
            """
            {
              "schemaVersion": 4,
              "projects": [{
                "id": "galaxies-reborn",
                "displayName": "Galaxies Reborn",
                "path": "galaxies-reborn",
                "available": true,
                "flavors": [{
                  "id": "cu", "displayName": "CU", "path": "galaxies-reborn/cu",
                  "available": true, "variants": []
                }],
                "variants": []
              }]
            }
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => ChannelManifestService.LoadUmbrellaCatalogAsync(stream));
    }

    [Fact]
    public async Task LoadUmbrellaCatalogRejectsUnsupportedSchema()
    {
        const string json = """{ "schemaVersion": 3, "projects": [] }""";

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => ChannelManifestService.LoadUmbrellaCatalogAsync(stream));
    }
}
