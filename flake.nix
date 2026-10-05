{
  description = "Once Campfire — ASP.NET Core development environment";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
  };

  outputs =
    { nixpkgs, ... }:
    let
      # sdk_11_0 is published for these systems. x86_64-darwin is not in its meta.platforms.
      systems = [
        "x86_64-linux"
        "aarch64-linux"
        "aarch64-darwin"
      ];

      forAllSystems =
        f:
        nixpkgs.lib.genAttrs systems (
          system:
          f {
            pkgs = import nixpkgs { inherit system; };
          }
        );

      campfireServer =
        pkgs:
        pkgs.writeShellApplication {
          name = "campfire-server";
          runtimeInputs = [
            pkgs.dotnetCorePackages.sdk_11_0
            pkgs.git
            pkgs.coreutils
            pkgs.gnugrep
            pkgs.util-linux
            pkgs.cacert
          ];
          runtimeEnv = {
            DOTNET_ROOT = "${pkgs.dotnetCorePackages.sdk_11_0}/share/dotnet";
            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1";
            DOTNET_MULTILEVEL_LOOKUP = "0";
            SSL_CERT_FILE = "${pkgs.cacert}/etc/ssl/certs/ca-bundle.crt";
            NIX_SSL_CERT_FILE = "${pkgs.cacert}/etc/ssl/certs/ca-bundle.crt";
          };
          meta = {
            mainProgram = "campfire-server";
            description = "Run the ASP.NET Core Campfire server and stop it on exit";
          };
          text = builtins.readFile ./nix/campfire-server.sh;
        };
    in
    {
      devShells = forAllSystems (
        { pkgs }:
        let
          # The SDK ships the matching ASP.NET Core runtime and targeting pack.
          dotnetSdk = pkgs.dotnetCorePackages.sdk_11_0;
        in
        {
          default = pkgs.mkShell {
            packages = [
              dotnetSdk

              # Same native pieces the Rails app uses for storage, jobs, and media.
              pkgs.sqlite
              pkgs.redis
              pkgs.ffmpeg
              pkgs.vips

              pkgs.pkg-config
              pkgs.openssl
              pkgs.icu
              pkgs.cacert
              pkgs.curl
              pkgs.git

              pkgs.nixfmt
            ];

            env = {
              DOTNET_ROOT = "${dotnetSdk}/share/dotnet";
              DOTNET_CLI_TELEMETRY_OPTOUT = "1";
              DOTNET_NOLOGO = "1";
              DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1";
              DOTNET_SKIP_WORKLOAD_INTEGRITY_CHECK = "1";
              # Do not pick up a distro-wide install beside this SDK.
              DOTNET_MULTILEVEL_LOOKUP = "0";
              SSL_CERT_FILE = "${pkgs.cacert}/etc/ssl/certs/ca-bundle.crt";
              NIX_SSL_CERT_FILE = "${pkgs.cacert}/etc/ssl/certs/ca-bundle.crt";
            };

            shellHook = ''
              export LD_LIBRARY_PATH="${
                pkgs.lib.makeLibraryPath [
                  pkgs.vips
                  pkgs.sqlite
                  pkgs.openssl
                  pkgs.icu
                  pkgs.ffmpeg
                  pkgs.stdenv.cc.cc
                  pkgs.zlib
                ]
              }''${LD_LIBRARY_PATH:+:}$LD_LIBRARY_PATH"

              echo "once-campfire-dotnet"
              echo "  SDK: $(dotnet --version)"
              echo "  redis-server, sqlite3, ffmpeg, and vips are on PATH"
              echo "  nix run .#server -- [--port PORT] starts the ASP.NET Core server"
            '';
          };
        }
      );

      packages = forAllSystems (
        { pkgs }: {
          server = campfireServer pkgs;
        }
      );

      apps = forAllSystems (
        { pkgs }: {
          server = {
            type = "app";
            program = "${campfireServer pkgs}/bin/campfire-server";
          };
        }
      );

      formatter = forAllSystems ({ pkgs }: pkgs.nixfmt);
    };
}
