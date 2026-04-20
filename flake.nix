{
  description = "Living The Dream Toolkit - Texture processor CLI and GUI";

  inputs = {
    flake-parts.url = "github:hercules-ci/flake-parts";
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
  };

  outputs =
    inputs@{ flake-parts, ... }:
    flake-parts.lib.mkFlake { inherit inputs; } {
      systems = [
        "x86_64-linux"
        "aarch64-linux"
      ];
      perSystem =
        { pkgs, ... }:
        {
          packages = {
            default = pkgs.buildDotnetModule {
              pname = "livin-the-dream-toolkit";
              version = "1.0.0";

              src = ./.;

              projectFile = "./LivinTheDreamToolkit.Gui/LivinTheDreamToolkit.Gui.csproj";
              dotnet-sdk = pkgs.dotnetCorePackages.sdk_8_0;
              dotnet-runtime = pkgs.dotnetCorePackages.runtime_8_0;

              nugetDeps = ./deps.json;

              meta.mainProgram = "LivinTheDreamToolkit.Gui";
            };
          };
        };
    };
}
