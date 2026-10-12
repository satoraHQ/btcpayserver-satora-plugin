# Satora BTCPay plugin: build, test and packaging tasks
# https://github.com/casey/just

plugin := "BTCPayServer.Plugins.Satora"
tests := "BTCPayServer.Plugins.Satora.Tests"
configuration := "Release"
tfm := "net10.0"
out := plugin / "bin" / configuration / tfm
dist := "dist"

# A client-sdk/dotnet-sdk checkout to build against instead of the Satora.Sdk package
SatoraSdkPath := env("SatoraSdkPath", "")
sdk := if SatoraSdkPath == "" { "" } else { "-p:SatoraSdkPath=" + quote(SatoraSdkPath) }

# List available recipes
default:
    @just --list

# Build the plugin
build:
    dotnet build {{plugin}} -c {{configuration}} {{sdk}}

# Run the unit tests
test:
    dotnet test --project {{tests}} -c {{configuration}} {{sdk}}

# Package an installable .btcpay into dist/, from a fresh build output so nothing stale ships
pack:
    rm -rf {{out}} {{dist}}
    dotnet build {{plugin}} -c {{configuration}} {{sdk}}
    dotnet run --project btcpayserver/BTCPayServer.PluginPacker -c {{configuration}} -- {{out}} {{plugin}} {{dist}}

# Check that the packaged .btcpay carries the Linux native libraries BTCPay's Docker images load
verify-pack:
    #!/usr/bin/env bash
    set -euo pipefail
    pkgs=({{dist}}/{{plugin}}/*/{{plugin}}.btcpay)
    [ -f "${pkgs[0]}" ] || { echo "no .btcpay under {{dist}}, run just pack first" >&2; exit 1; }
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT
    unzip -q "${pkgs[0]}" -d "$tmp"
    status=0
    for pair in linux-x64:x86-64 linux-arm64:aarch64; do
        rid=${pair%%:*}
        arch=${pair#*:}
        lib=runtimes/$rid/native/libsatora_sdk_ffi.so
        if [ -f "$tmp/$lib" ] && file -b "$tmp/$lib" | grep -q "ELF 64-bit.*$arch"; then
            echo "ok: $lib"
        else
            echo "missing: $lib ($arch ELF shared object)" >&2
            status=1
        fi
    done
    exit $status

# Remove build output and packaged artifacts
clean:
    rm -rf {{plugin}}/bin {{plugin}}/obj {{dist}} {{tests}}/bin {{tests}}/obj
