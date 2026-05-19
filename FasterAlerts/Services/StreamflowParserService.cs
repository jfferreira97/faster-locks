using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using FasterAlerts.Models;
using Microsoft.Extensions.Logging;

namespace FasterAlerts.Services;

public class StreamflowParserService(ILogger<StreamflowParserService> logger)
{
    private static readonly HashSet<string> StreamflowProgramIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "strmRqUCoQUgGUan5YhzUZa6KqdzwX5L6FpUxfmKg5m",
        "aSTRM2NKoKxNnkmLWk9sz3k74gKBk9t7bpPrTGxMszH"
    };

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public Task<StreamAlert?> ParseAsync(EnhancedTransaction tx)
    {
        if (tx.TransactionError is not null) return Task.FromResult<StreamAlert?>(null);

        var sfIx = tx.Instructions
            .FirstOrDefault(ix => ix.ProgramId is not null && StreamflowProgramIds.Contains(ix.ProgramId));

        if (sfIx is null) return Task.FromResult<StreamAlert?>(null);

        logger.LogInformation("📦 Streamflow tx {Sig} | feePayer={FeePayer} | transfers={Transfers} | accounts[0..5]={Accounts}",
            tx.Signature?[..8],
            tx.FeePayer?[..8],
            JsonSerializer.Serialize(tx.TokenTransfers.Select(t => new {
                from = t.FromUserAccount?[..8],
                to = t.ToUserAccount?[..8],
                mint = t.Mint?[..8],
                amount = t.TokenAmount
            })),
            string.Join(", ", sfIx.Accounts.Take(6).Select(a => a[..8])));

        var transfer = tx.TokenTransfers
            .FirstOrDefault(t => t.FromUserAccount == tx.FeePayer && t.TokenAmount > 0);

        if (transfer is null)
        {
            logger.LogInformation("⏭  skip {Sig} — no outbound from feePayer, probably withdraw/cancel", tx.Signature?[..8]);
            return Task.FromResult<StreamAlert?>(null);
        }

        var streamAccount = sfIx.Accounts.ElementAtOrDefault(0) ?? "";
        var recipient = sfIx.Accounts.ElementAtOrDefault(5)
                     ?? sfIx.Accounts.ElementAtOrDefault(4)
                     ?? "";

        var txTime = DateTimeOffset.FromUnixTimeSeconds(tx.Timestamp);

        // Decode vesting params from Borsh-encoded instruction data
        DateTimeOffset? unlockDate = null;
        int cliffDays = -1, vestingDays = -1;

        var decoded = TryDecodeCreateParams(sfIx.Data);
        if (decoded.HasValue)
        {
            var (startTime, cliff, period, amtPerPeriod, netAmount) = decoded.Value;
            var start = DateTimeOffset.FromUnixTimeSeconds(startTime);

            if (cliff > startTime)
            {
                unlockDate = DateTimeOffset.FromUnixTimeSeconds(cliff);
                cliffDays = (int)Math.Round((cliff - startTime) / 86400.0);
            }
            else if (period > 0)
            {
                // No cliff — first withdrawal is after one period
                unlockDate = DateTimeOffset.FromUnixTimeSeconds(startTime + period);
                cliffDays = 0;
            }

            if (amtPerPeriod > 0 && period > 0)
            {
                var totalPeriods = (long)Math.Ceiling((double)netAmount / amtPerPeriod);
                var vestingEndUnix = startTime + totalPeriods * period;
                vestingDays = (int)Math.Round((vestingEndUnix - startTime) / 86400.0);
            }

            logger.LogInformation("📐 decoded | cliff={CliffDays}d | vesting={VestingDays}d | unlockDate={Unlock}",
                cliffDays, vestingDays, unlockDate?.ToString("yyyy-MM-dd"));
        }
        else
        {
            logger.LogInformation("⚠️  could not decode instruction data for {Sig}", tx.Signature?[..8]);
        }

        logger.LogInformation("🔒 stream CREATE | mint={Mint} | amount={Amount:N0} | creator={Creator} | recipient={Recipient}",
            transfer.Mint?[..8], transfer.TokenAmount, tx.FeePayer?[..8], recipient?[..8]);

        return Task.FromResult<StreamAlert?>(new StreamAlert
        {
            Signature = tx.Signature ?? "",
            StreamAccount = streamAccount,
            TokenMint = transfer.Mint ?? "",
            AmountLocked = transfer.TokenAmount,
            CreatorWallet = tx.FeePayer ?? "",
            RecipientWallet = recipient,
            CliffDays = cliffDays,
            VestingDays = vestingDays,
            UnlockDate = unlockDate,
            Timestamp = txTime
        });
    }

    // Borsh-decodes Streamflow CreateStream instruction params.
    // Layout after 8-byte Anchor discriminator:
    //   start_time (u64), net_amount (u64), period (u64), amount_per_period (u64), cliff (u64), ...
    private static (long startTime, long cliff, long period, ulong amtPerPeriod, ulong netAmount)? TryDecodeCreateParams(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        try
        {
            var bytes = Base58Decode(data);
            if (bytes.Length < 48) return null;

            var startTime = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8, 8));
            var netAmount  = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16, 8));
            var period     = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(24, 8));
            var amtPerPd   = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32, 8));
            var cliff      = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(40, 8));

            return (startTime, cliff, period, amtPerPd, netAmount);
        }
        catch { return null; }
    }

    private const string B58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    private static byte[] Base58Decode(string input)
    {
        var bigInt = BigInteger.Zero;
        foreach (var c in input)
        {
            var digit = B58.IndexOf(c);
            if (digit < 0) throw new FormatException($"Invalid Base58 char: {c}");
            bigInt = bigInt * 58 + digit;
        }

        var bytes = bigInt.ToByteArray(isUnsigned: true, isBigEndian: true);

        var leading = input.TakeWhile(c => c == '1').Count();
        if (leading == 0) return bytes;

        var result = new byte[leading + bytes.Length];
        bytes.CopyTo(result, leading);
        return result;
    }
}
