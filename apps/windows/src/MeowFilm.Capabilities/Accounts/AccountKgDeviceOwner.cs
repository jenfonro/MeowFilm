// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using MeowFilm.Platform.Storage;

namespace MeowFilm.Capabilities.Accounts;

public sealed partial class MusicAccountOwner
{
    private async Task<StoredKgDevice> AcquireKgDevice(Operation op, StoredAccount account, CancellationToken authority)
    {
        Guard(op);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(op.Token, authority);
        var device = await DiskAsync(async () =>
        {
            var document = await ReadDocument(linked.Token).ConfigureAwait(false);
            Guard(op);
            CheckKgAccount(document, account);
            return document.GetKgDevice();
        }, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        if (device.Generation != 0) return device;
        // Identity must be durably acknowledged BEFORE even the registration request.
        return await SaveKgDevice(op, account, device, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            null, linked.Token).ConfigureAwait(false);
    }
    private static void CheckKgAccount(AccountDocument document, StoredAccount before)
    {
        var current = document.Get("kg");
        AccountRules.Require(current.Generation == before.Generation &&
            current.ValueFingerprint == before.ValueFingerprint, "STORAGE_CONFLICT");
    }
    private async Task<StoredKgDevice> SaveKgDevice(Operation op, StoredAccount account,
        StoredKgDevice expected, string seed, string? dfid, CancellationToken authority)
    {
        Guard(op);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(op.Token, authority);
        var token = linked.Token;
        bool admitted = false;
        var physical = DiskAsync(async () =>
        {
            Guard(op);
            var document = await ReadDocument(token).ConfigureAwait(false);
            Guard(op); token.ThrowIfCancellationRequested();
            CheckKgAccount(document, account);
            byte[] bytes = document.ReplaceKgDevice(expected, seed, dfid);
            var next = document.GetKgDevice();
            try
            {
                lock (gate)
                {
                    Guard(op); token.ThrowIfCancellationRequested();
                    states["kg"].Uncertain = true; admitted = true; op.DeviceAdmitted = true;
                }
                try
                {
                    var actual = await store.WriteAsync(DocumentNamespace, bytes, document.Fingerprint, token).ConfigureAwait(false);
                    AccountRules.Require(actual == DocumentFingerprint.FromBytes(bytes), "COMMIT_UNKNOWN");
                }
                catch (ProtectedDocumentException e) when (e.Error == DocumentStoreError.Conflict)
                {
                    lock (gate)
                    {
                        op.DeviceAdmitted = false;
                        if (states["kg"].Epoch == op.Epoch) states["kg"].Uncertain = false;
                    }
                    throw new MusicAccountException("STORAGE_CONFLICT");
                }
                catch { throw new MusicAccountException("COMMIT_UNKNOWN"); }
                return next;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }, token);
        try
        {
            var saved = await physical.WaitAsync(token).ConfigureAwait(false);
            lock (gate)
            {
                Guard(op); token.ThrowIfCancellationRequested();
                states["kg"].Uncertain = false; op.DeviceAdmitted = false;
            }
            return saved;
        }
        catch (Exception error)
        {
            if (AccountRules.Error(error) == "STORAGE_CONFLICT") throw;
            if (admitted) throw new MusicAccountException("COMMIT_UNKNOWN");
            throw;
        }
    }
    private async Task CheckKgDevice(Operation op, StoredKgDevice expected)
    {
        var current = await DiskAsync(async () => (await ReadDocument(op.Token).ConfigureAwait(false)).GetKgDevice(),
            op.Token).WaitAsync(op.Token).ConfigureAwait(false);
        Guard(op);
        AccountRules.Require(StoredKgDevice.Same(current, expected), "STORAGE_CONFLICT");
    }
}
