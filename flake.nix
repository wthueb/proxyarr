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
          dotnet-sdk = pkgs.dotnetCorePackages.sdk_10_0;
        in
        {
          devShells.default = pkgs.mkShell {
            packages = [ dotnet-sdk ];

            env.DOTNET_ROOT = "${dotnet-sdk}/share/dotnet";
          };

          treefmt = {
            projectRootFile = "flake.nix";

            settings.formatter.dotnet-format = {
              command = "${dotnet-sdk}/bin/dotnet";
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
              csharpier.enable = true;
              nixfmt.enable = true;
            };
          };
        };
    };
}
