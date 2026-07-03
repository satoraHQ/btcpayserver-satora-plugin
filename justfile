# Satora BTCPay plugin — build & packaging tasks
# https://github.com/casey/just

plugin := "BTCPayServer.Plugins.Satora"
configuration := "Release"
tfm := "net10.0"

# List available recipes
default:
    @just --list

# Build the plugin
build:
    dotnet build {{plugin}} -c {{configuration}}

# Build and package an installable .btcpay into {{plugin}}/dist
package: build
    dotnet run --project btcpayserver/BTCPayServer.PluginPacker -- \
        {{plugin}}/bin/{{configuration}}/{{tfm}} \
        {{plugin}} \
        {{plugin}}/dist

# Remove build output and packaged artifacts
clean:
    rm -rf {{plugin}}/bin {{plugin}}/obj {{plugin}}/dist
