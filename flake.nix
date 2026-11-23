{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";
    flake-parts.url = "github:hercules-ci/flake-parts";
    treefmt-nix = {
      url = "github:numtide/treefmt-nix";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs =
    inputs@{ flake-parts, treefmt-nix, ... }:
    flake-parts.lib.mkFlake { inherit inputs; } {
      imports = [ treefmt-nix.flakeModule ];

      systems = [
        "x86_64-linux"
        "aarch64-linux"
        "aarch64-darwin"
      ];

      perSystem =
        { pkgs, ... }:
        let
          dotnet-sdk = pkgs.dotnetCorePackages.sdk_11_0;
        in
        {
          devShells.default = pkgs.mkShellNoCC {
            packages = [ dotnet-sdk ];

            env.DOTNET_ROOT = "${dotnet-sdk}/share/dotnet";
          };

          treefmt = {
            projectRootFile = "flake.nix";

            settings.formatter.dotnet-format = {
              command = pkgs.writeShellScriptBin "dotnet-format" ''
                exec ${dotnet-sdk}/bin/dotnet "$@" </dev/null
              '';
              options = [
                "format"
                "--no-restore"
                "--include"
              ];
              includes = [
                "*.cs"
                "*.sln"
                "*.csproj"
              ];
            };

            programs = {
              actionlint.enable = true;
              csharpier = {
                enable = true;
                package = pkgs.writeShellScriptBin "csharpier" ''
                  exec ${pkgs.csharpier}/bin/csharpier "$@" </dev/null
                '';
              };
              nixfmt.enable = true;
            };
          };
        };
    };
}
