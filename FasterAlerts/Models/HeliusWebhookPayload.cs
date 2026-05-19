using System.Text.Json.Serialization;

namespace FasterAlerts.Models;

public class EnhancedTransaction
{
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("feePayer")]
    public string? FeePayer { get; set; }

    [JsonPropertyName("fee")]
    public long Fee { get; set; }

    [JsonPropertyName("transactionError")]
    public object? TransactionError { get; set; }

    [JsonPropertyName("tokenTransfers")]
    public List<TokenTransfer> TokenTransfers { get; set; } = [];

    [JsonPropertyName("nativeTransfers")]
    public List<NativeTransfer> NativeTransfers { get; set; } = [];

    [JsonPropertyName("accountData")]
    public List<AccountData> AccountData { get; set; } = [];

    [JsonPropertyName("instructions")]
    public List<Instruction> Instructions { get; set; } = [];
}

public class TokenTransfer
{
    [JsonPropertyName("fromUserAccount")]
    public string? FromUserAccount { get; set; }

    [JsonPropertyName("toUserAccount")]
    public string? ToUserAccount { get; set; }

    [JsonPropertyName("fromTokenAccount")]
    public string? FromTokenAccount { get; set; }

    [JsonPropertyName("toTokenAccount")]
    public string? ToTokenAccount { get; set; }

    [JsonPropertyName("tokenAmount")]
    public decimal TokenAmount { get; set; }

    [JsonPropertyName("decimals")]
    public int Decimals { get; set; }

    [JsonPropertyName("mint")]
    public string? Mint { get; set; }

    [JsonPropertyName("tokenStandard")]
    public string? TokenStandard { get; set; }
}

public class NativeTransfer
{
    [JsonPropertyName("fromUserAccount")]
    public string? FromUserAccount { get; set; }

    [JsonPropertyName("toUserAccount")]
    public string? ToUserAccount { get; set; }

    [JsonPropertyName("amount")]
    public long Amount { get; set; }
}

public class AccountData
{
    [JsonPropertyName("account")]
    public string? Account { get; set; }

    [JsonPropertyName("nativeBalanceChange")]
    public long NativeBalanceChange { get; set; }

    [JsonPropertyName("tokenBalanceChanges")]
    public List<TokenBalanceChange> TokenBalanceChanges { get; set; } = [];
}

public class TokenBalanceChange
{
    [JsonPropertyName("userAccount")]
    public string? UserAccount { get; set; }

    [JsonPropertyName("tokenAccount")]
    public string? TokenAccount { get; set; }

    [JsonPropertyName("mint")]
    public string? Mint { get; set; }

    [JsonPropertyName("rawTokenAmount")]
    public RawTokenAmount? RawTokenAmount { get; set; }
}

public class RawTokenAmount
{
    [JsonPropertyName("tokenAmount")]
    public string? TokenAmount { get; set; }

    [JsonPropertyName("decimals")]
    public int Decimals { get; set; }
}

public class Instruction
{
    [JsonPropertyName("accounts")]
    public List<string> Accounts { get; set; } = [];

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("programId")]
    public string? ProgramId { get; set; }

    [JsonPropertyName("innerInstructions")]
    public List<InnerInstruction> InnerInstructions { get; set; } = [];
}

public class InnerInstruction
{
    [JsonPropertyName("accounts")]
    public List<string> Accounts { get; set; } = [];

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("programId")]
    public string? ProgramId { get; set; }
}
