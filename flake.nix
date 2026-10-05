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

      # Native gems and libvips are loaded by the Rails process. `nix run` does not
      # apply a dev-shell hook, so the app exports these paths itself.
      libraryPath =
        pkgs:
        pkgs.lib.makeLibraryPath [
          pkgs.stdenv.cc.cc.lib
          pkgs.sqlite
          pkgs.openssl
          pkgs.zlib
          pkgs.libyaml
          pkgs.libffi
          pkgs.libxml2
          pkgs.libxslt
          pkgs.vips
          pkgs.glib
          pkgs.ffmpeg.lib
          pkgs.icu
        ];

      pkgConfigPath =
        pkgs:
        pkgs.lib.makeSearchPathOutput "dev" "lib/pkgconfig" [
          pkgs.sqlite
          pkgs.openssl
          pkgs.zlib
          pkgs.libyaml
          pkgs.libffi
          pkgs.libxml2
          pkgs.libxslt
          pkgs.vips
          pkgs.glib
          pkgs.ffmpeg
          pkgs.icu
        ];

      campfireServer =
        pkgs:
        pkgs.writeShellApplication {
          name = "campfire-server";
          runtimeInputs = [
            pkgs.ruby_3_4
            pkgs.git
            pkgs.redis
            pkgs.sqlite
            pkgs.ffmpeg
            pkgs.vips
            pkgs.pkg-config
            pkgs.gnumake
            pkgs.stdenv.cc
            pkgs.coreutils
            pkgs.gnugrep
            pkgs.openssl
          ];
          runtimeEnv = {
            CC = "gcc";
            CXX = "g++";
            PKG_CONFIG_PATH = pkgConfigPath pkgs;
            LD_LIBRARY_PATH = libraryPath pkgs;
            LIBRARY_PATH = libraryPath pkgs;
            SSL_CERT_FILE = "${pkgs.cacert}/etc/ssl/certs/ca-bundle.crt";
            GIT_SSL_CAINFO = "${pkgs.cacert}/etc/ssl/certs/ca-bundle.crt";
            NIX_SSL_CERT_FILE = "${pkgs.cacert}/etc/ssl/certs/ca-bundle.crt";
          };
          meta = {
            mainProgram = "campfire-server";
            description = "Run the Campfire development server and a private Redis, then stop both on exit";
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
              echo "  nix run .#server -- [--port PORT] starts Campfire and Redis"
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
