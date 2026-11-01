{
  description = "Interstate '76 reimplemented on Godot 4 (C#), reading the original install at runtime";

  # unstable for Godot 4.7's .NET build; 25.05 carries 4.4.
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { self, nixpkgs }:
    let
      system = "x86_64-linux";
      pkgs = import nixpkgs {
        inherit system;
        # steam-run (testing the release builds in a plain FHS environment) is the only unfree package here
        config.allowUnfreePredicate = p: builtins.elem (nixpkgs.lib.getName p) [ "steam-run" "steam-unwrapped" "steam" ];
      };
    in {
      # The mono export templates of the same Godot as the devShell's editor (tools/release.sh exports with them).
      packages.${system}.export-templates = pkgs.godotPackages.export-templates-mono-bin;
      devShells.${system}.default = pkgs.mkShell {
        packages = [
          pkgs.dotnet-sdk_8
          pkgs.godot_4-mono
          pkgs.python3
          pkgs.jq
          # tools/shot.sh renders on the GPU in a private headless Weston
          pkgs.weston
          pkgs.git
          pkgs.unzip
          # the comparison sheets (contact sheets)
          pkgs.imagemagick
          # runs an unpacked release zip as a player's machine would (tools/release.sh's output)
          pkgs.steam-run
          # signs the macOS bundle again (tools/release.sh): Godot's own signer writes entitlements macOS rejects
          pkgs.rcodesign
        ];
        shellHook = ''
          export DOTNET_CLI_TELEMETRY_OPTOUT=1
          export DOTNET_NOLOGO=1
          export DOTNET_ROOT=${pkgs.dotnet-sdk_8}/share/dotnet
        '';
      };
    };
}
