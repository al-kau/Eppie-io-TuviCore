﻿using KeyDerivation;
using KeyDerivation.Keys;
using KeyDerivationLib;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tuvi.Core.Backup;
using Tuvi.Core.DataStorage;
using Tuvi.Core.Entities;
using Tuvi.Core.Mail;
using TuviPgpLib;
using TuviPgpLib.Entities;

namespace Tuvi.Core.Impl.SecurityManagement
{
    public static class SecurityManagerCreator
    {
        public static ISecurityManager GetSecurityManager(
            IDataStorage storage,
            ITuviPgpContext pgpContext,
            IMessageProtector messageProtector,
            IBackupProtector backupProtector)
        {
            return new SecurityManager(storage, pgpContext, messageProtector, backupProtector);
        }

        public static ISeedQuiz CreateSeedQuiz(string[] seedPhrase)
        {
            return new SeedQuiz(seedPhrase);
        }
    }

    internal class SecurityManager : ISecurityManager
    {
        /// <param name="backupProtector">Setup of protector is performed here.</param>
        public SecurityManager(
            IDataStorage storage,
            ITuviPgpContext pgpContext,
            IMessageProtector messageProtector,
            IBackupProtector backupProtector)
        {
            SeedValidator = new SeedValidator();
            KeyStorage = storage;
            DataStorage = storage;
            PgpContext = pgpContext;
            MessageProtector = messageProtector;
            BackupProtector = backupProtector;
        }

        private void InitializeManager()
        {
            KeyFactory = new MasterKeyFactory(KeyDerivationDetails);
            SpecialPgpKeyIdentities = KeyDerivationDetails.GetSpecialPgpKeyIdentities();
            BackupProtector.SetPgpKeyIdentity(SpecialPgpKeyIdentities[SpecialPgpKeyType.Backup]);
        }

        public void SetKeyDerivationDetails(IKeyDerivationDetailsProvider keyDerivationDetailsProvider)
        {
            if (keyDerivationDetailsProvider is null)
            {
                throw new ArgumentNullException(nameof(keyDerivationDetailsProvider));
            }

            KeyDerivationDetails = keyDerivationDetailsProvider;
            InitializeManager();
        }

        public async Task<bool> IsSeedPhraseInitializedAsync(CancellationToken cancellationToken = default)
        {
            return await KeyStorage.IsMasterKeyExistAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<string[]> CreateSeedPhraseAsync()
        {
            if (KeyFactory is null)
            {
                throw new InvalidOperationException($"{nameof(KeyFactory)} is not initialized.");
            }

            var seed = await Task.Run(() => KeyFactory.GenerateSeedPhrase()).ConfigureAwait(false);
            MasterKey = await Task.Run(() => KeyFactory.GetMasterKey()).ConfigureAwait(false);
            SeedQuiz = new SeedQuiz(seed);
            return seed;
        }

        public async Task RestoreSeedPhraseAsync(string[] seedPhrase)
        {
            if (KeyFactory is null)
            {
                throw new InvalidOperationException($"{nameof(KeyFactory)} is not initialized.");
            }

            await Task.Run(() => KeyFactory.RestoreSeedPhrase(seedPhrase)).ConfigureAwait(false);
            MasterKey = await Task.Run(() => KeyFactory.GetMasterKey()).ConfigureAwait(false);
        }

        public async Task StartAsync(string password, CancellationToken cancellationToken = default)
        {
            try
            {
                if (await DataStorage.IsStorageExistAsync(cancellationToken).ConfigureAwait(false))
                {
                    await DataStorage.OpenAsync(password, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await DataStorage.CreateAsync(password, cancellationToken).ConfigureAwait(false);
                }

                await PgpContext.LoadContextAsync().ConfigureAwait(false);

                if (await KeyStorage.IsMasterKeyExistAsync(cancellationToken).ConfigureAwait(false))
                {
                    await LoadMasterKeyAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await InitializeSeedPhraseAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (DataBasePasswordException)
            {
                throw;
            }
        }

        public async Task InitializeSeedPhraseAsync(CancellationToken cancellationToken = default)
        {
            if (MasterKey != null)
            {
                await KeyStorage.InitializeMasterKeyAsync(MasterKey, cancellationToken).ConfigureAwait(false);
                await CreateDefaultPgpKeysForAllAccountsAsync(cancellationToken).ConfigureAwait(false);
                CreateSpecialPgpKeys();
            }
        }

        public async Task ResetAsync()
        {
            // TODO Zero master key
            MasterKey = null;
            SeedQuiz = null;
            await DataStorage.ResetAsync().ConfigureAwait(false);
        }

        public async Task ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken)
        {
            await DataStorage.ChangePasswordAsync(currentPassword, newPassword, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> IsNeverStartedAsync(CancellationToken cancellationToken)
        {
            return !await DataStorage.IsStorageExistAsync(cancellationToken).ConfigureAwait(false);
        }

        public ISeedQuiz GetSeedQuiz()
        {
            return SeedQuiz;
        }

        public ISeedValidator GetSeedValidator()
        {
            return SeedValidator;
        }

        private async Task LoadMasterKeyAsync(CancellationToken cancellationToken = default)
        {
            MasterKey = await KeyStorage.GetMasterKeyAsync(cancellationToken).ConfigureAwait(false);
        }

        private void CreateSpecialPgpKeys()
        {
            foreach (var specialKey in SpecialPgpKeyIdentities)
            {
                string keyIdentity = specialKey.Value;
                var dummyBackupAddress = new EmailAddress(keyIdentity);

                if (!PgpContext.IsSecretKeyExist(dummyBackupAddress.ToUserIdentity()))
                {
                    PgpContext.DeriveKeyPair(MasterKey, keyIdentity);
                }
            }
        }

        private async Task CreateDefaultPgpKeysForAllAccountsAsync(CancellationToken cancellationToken = default)
        {
            var accounts = await DataStorage.GetAccountsAsync(cancellationToken).ConfigureAwait(false);

            foreach (var account in accounts)
            {
                CreateDefaultPgpKeys(account);
            }
        }

        public void CreateDefaultPgpKeys(Account account)
        {
            if (MasterKey == null)
            {
                return;
            }
            if (account == null)
            {
                throw new PgpArgumentNullException(nameof(account));
            }

            if (!PgpContext.IsSecretKeyExist(account.Email.ToUserIdentity()))
            {
                PgpContext.DeriveKeyPair(MasterKey, account.GetPgpUserIdentity(), account.GetPgpKeyTag());
            }
        }

        public ICollection<PgpKeyInfo> GetPublicPgpKeysInfo()
        {
            List<PgpKeyInfo> keys = PgpContext.GetPublicKeysInfo().ToList();
            keys.RemoveAll(key => IsServiceKey(key));
            return keys;
        }

        private bool IsServiceKey(PgpKeyInfo pgpKey)
        {
            foreach (var specialKey in SpecialPgpKeyIdentities)
            {
                if (pgpKey.UserIdentity.Contains(specialKey.Value))
                {
                    return true;
                }
            }
            return false;
        }

        public void ImportPublicPgpKey(byte[] keyData)
        {
            using (var stream = new MemoryStream(keyData))
            {
                PgpContext.ImportPublicKeys(stream, true);
            }
        }

        public void ImportPgpKeyRingBundle(Stream keyBundle)
        {
            using (ArmoredInputStream keyIn = new ArmoredInputStream(keyBundle))
            {
                var header = keyIn.GetArmorHeaderLine();
                if (header?.IndexOf("private", StringComparison.InvariantCultureIgnoreCase) >= 0)
                {
                    PgpContext.ImportSecretKeys(keyIn, false);
                    return;
                }
                if (header?.IndexOf("public", StringComparison.InvariantCultureIgnoreCase) >= 0)
                {
                    PgpContext.ImportPublicKeys(keyIn, false);
                    return;
                }
            }
            throw new PgpException("Stream does not contain any key bundle.");
        }

        public Task ExportPgpKeyRingAsync(long keyId, Stream stream, CancellationToken cancellationToken)
        {
            return PgpContext.ExportPublicKeyRingAsync(keyId, stream, cancellationToken);
        }

        public int GetRequiredSeedPhraseLength()
        {
            if (KeyDerivationDetails is null)
            {
                throw new InvalidOperationException($"{nameof(KeyDerivationDetails)} is not set.");
            }

            return KeyDerivationDetails.GetSeedPhraseLength();
        }

        public IMessageProtector GetMessageProtector()
        {
            return MessageProtector;
        }

        public IBackupProtector GetBackupProtector()
        {
            return BackupProtector;
        }

        private MasterKey MasterKey;
        private SeedQuiz SeedQuiz;
        private IKeyDerivationDetailsProvider KeyDerivationDetails;
        private MasterKeyFactory KeyFactory;
        private Dictionary<SpecialPgpKeyType, string> SpecialPgpKeyIdentities;
        private readonly SeedValidator SeedValidator;
        private readonly IKeyStorage KeyStorage;
        private readonly IDataStorage DataStorage;
        private readonly ITuviPgpContext PgpContext;
        private readonly IMessageProtector MessageProtector;
        private readonly IBackupProtector BackupProtector;
    }
}
