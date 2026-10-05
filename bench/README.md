# Message benchmarks

`campfire-bench` is the C# port of the original Ruby drivers. It uses the .NET SDK in the development flake; no separate load generator is required. Provide a frozen baseline source directory, an isolated seed containing `db/`, `storage/` and `labels.json`, and a matching application image with compiled assets.

```sh
nix develop
dotnet run --project bench/src/Campfire.Bench.Cli -- compare-hot-paths --baseline PATH --baseline-ref SHA --seed SEED
dotnet run --project bench/src/Campfire.Bench.Cli -- compare-http --baseline PATH --seed SEED
dotnet run --project bench/src/Campfire.Bench.Cli -- compare-http --baseline PATH --seed SEED --paths sidebar,search --concurrencies 16 --duration 10
```

`measure` hits one server that is already running, with the same keep-alive client:

```sh
dotnet run --project bench/src/Campfire.Bench.Cli -- measure --base http://127.0.0.1:3000 --seed SEED --warmup 0
```

Both comparison drivers alternate before/after order and reset fixture storage for each run. Results default to ignored `tmp/rails-optimization/results/`; override with `--output PATH`. Use `--image` to override `campfire-reference:app`, and `--cpus` to override server CPUs `8-11`. HTTP clients use CPUs `12-15` by default (`--client-cpus`). `--help` lists options.

The rendering probe checks exact response bodies, selected headers and unread payloads, and records timing, queries and allocations with MemoryStore, frozen time and fixture-only CSRF disabling. Unread fanout excludes adapter I/O. That probe still runs `bench/message_hot_paths.rb` inside the Rails image, because it measures the Rails process directly. The driver that launches it, checks parity, and summarizes medians is C#.

The HTTP driver uses production Puma/Redis with one worker and five threads. Each client task keeps one connection alive, requests uncompressed responses, and reads the whole body. Login uses normal CSRF protection; all warmup and measured responses must be HTTP 200 without transport errors. Measurements exclude Thruster, TLS and gzip. Client CPU, JIT warmup and GC can affect throughput; repeat runs and check client saturation.

```sh
dotnet test bench/Campfire.Bench.slnx
```
