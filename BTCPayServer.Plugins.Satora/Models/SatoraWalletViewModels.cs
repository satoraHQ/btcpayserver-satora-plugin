namespace BTCPayServer.Plugins.Satora.Models;

// Form models for the wallet and settings POSTs. None carries a StoreId: the store always comes from the route.

public class SatoraSettingsViewModel
{
    public bool Enabled { get; set; }
}

public class SatoraGenerateWalletViewModel
{
    public string? ReplaceConfirmation { get; set; }
}

public class SatoraRestoreWalletViewModel
{
    public string? Seed { get; set; }

    public string? SeedConfirmation { get; set; }

    public string? ReplaceConfirmation { get; set; }
}

public class SatoraConfirmBackupViewModel
{
    public string? SeedConfirmation { get; set; }
}
