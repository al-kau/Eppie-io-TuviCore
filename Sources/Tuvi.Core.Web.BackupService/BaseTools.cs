﻿using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Tuvi.Core.Web.BackupService
{
    public static class BaseTools
    {
        public static async Task<bool> IsUploadAllowedAsync(IFormFileCollection files, BlobContainerClient blobContainerClient, string backupPgpKeyIdentity)
        {
            if (files is null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            if (blobContainerClient is null)
            {
                throw new ArgumentNullException(nameof(blobContainerClient));
            }

            var result = false;

            // There should be three files: public key, signature, backup.
            if (files.Count == 3)
            {
                var fingerprint = Path.GetFileNameWithoutExtension(files[0].FileName);

                var publicKeyName = fingerprint + DataIdentificators.PublicKeyExtension;
                var signatureName = fingerprint + DataIdentificators.SignatureExtension;
                var backupName = fingerprint + DataIdentificators.BackupExtension;

                var publicKeyFile = GetIFormFile(publicKeyName, files);
                var signatureFile = GetIFormFile(signatureName, files);
                var backupFile = GetIFormFile(backupName, files);

                var publicKeyBlobName = publicKeyName;
                var publicKeyBlobClient = blobContainerClient.GetBlobClient(publicKeyBlobName);
                var publicKeyExists = await publicKeyBlobClient.ExistsAsync().ConfigureAwait(false);

                if (publicKeyExists)
                {
                    //The public key has already been saved, we check the signature with it.
                    //And also check the signature using a new key.

                    var result1 = await VerifySignatureAsync(publicKeyBlobClient, signatureFile, backupFile, backupPgpKeyIdentity).ConfigureAwait(false);
                    var result2 = await VerifySignatureAsync(publicKeyFile, signatureFile, backupFile, backupPgpKeyIdentity).ConfigureAwait(false);

                    result = result1 && result2;
                }
                else
                {
                    //The public key has not yet been saved, we check the signature only with the new key.

                    result = await VerifySignatureAsync(publicKeyFile, signatureFile, backupFile, backupPgpKeyIdentity).ConfigureAwait(false);
                }
            }

            return result;
        }

        public static async Task<bool> VerifySignatureAsync(IFormFile publicKeyFile, IFormFile signatureFile, IFormFile backupFile, string backupPgpKeyIdentity)
        {
            if (publicKeyFile is null)
            {
                throw new ArgumentNullException(nameof(publicKeyFile));
            }

            if (signatureFile is null)
            {
                throw new ArgumentNullException(nameof(signatureFile));
            }

            if (backupFile is null)
            {
                throw new ArgumentNullException(nameof(backupFile));
            }

            var result = false;

            using (var newPublicKeyStream = new MemoryStream())
            using (var signatureStream = new MemoryStream())
            using (var backupStream = new MemoryStream())
            {
                await publicKeyFile.CopyToAsync(newPublicKeyStream).ConfigureAwait(false);
                await signatureFile.CopyToAsync(signatureStream).ConfigureAwait(false);
                await backupFile.CopyToAsync(backupStream).ConfigureAwait(false);

                result = await SignatureChecker.IsValidSignatureAsync(newPublicKeyStream, signatureStream, backupStream, backupPgpKeyIdentity).ConfigureAwait(false);
            }

            return result;
        }

        public static async Task<bool> VerifySignatureAsync(BlobClient publicKeyBlobClient, IFormFile signatureFile, IFormFile backupFile, string backupPgpKeyIdentity)
        {
            if (publicKeyBlobClient is null)
            {
                throw new ArgumentNullException(nameof(publicKeyBlobClient));
            }

            if (signatureFile is null)
            {
                throw new ArgumentNullException(nameof(signatureFile));
            }

            if (backupFile is null)
            {
                throw new ArgumentNullException(nameof(backupFile));
            }

            var result = false;

            using (var publicKeyStream = new MemoryStream())
            using (var signatureStream = new MemoryStream())
            using (var backupStream = new MemoryStream())
            {
                await publicKeyBlobClient.DownloadToAsync(publicKeyStream).ConfigureAwait(false);
                await signatureFile.CopyToAsync(signatureStream).ConfigureAwait(false);
                await backupFile.CopyToAsync(backupStream).ConfigureAwait(false);

                result = await SignatureChecker.IsValidSignatureAsync(publicKeyStream, signatureStream, backupStream, backupPgpKeyIdentity).ConfigureAwait(false);
            }

            return result;
        }

        public static async Task<string> GetFileCidAsync(string name, BlobContainerClient blobContainerClient)
        {
            if (blobContainerClient is null)
            {
                throw new ArgumentNullException(nameof(blobContainerClient));
            }

            var fingerprint = Path.GetFileNameWithoutExtension(name);
            var blobClient = blobContainerClient.GetBlobClient(fingerprint + DataIdentificators.CidExtension);

            if (await blobClient.ExistsAsync().ConfigureAwait(false))
            {
                using (var ms = new MemoryStream())
                {
                    await blobClient.DownloadToAsync(ms).ConfigureAwait(false);

                    var cid = Encoding.UTF8.GetString(ms.ToArray());

                    return cid;
                }
            }

            return null;
        }

        internal class JsonCid
        {
            public string cid { get; set; }
        }

        public static async Task SaveFileCidAsync(BlobContainerClient blobContainerClient, string name, string json)
        {
            if (blobContainerClient is null)
            {
                throw new ArgumentNullException(nameof(blobContainerClient));
            }

            var jsonCid = new JsonCid();
            jsonCid = JsonSerializer.Deserialize<JsonCid>(json);

            var fingerprint = Path.GetFileNameWithoutExtension(name);
            var blobClient = blobContainerClient.GetBlobClient(fingerprint + DataIdentificators.CidExtension);

            var array = Encoding.UTF8.GetBytes(jsonCid.cid);
            await blobClient.UploadAsync(BinaryData.FromBytes(array)).ConfigureAwait(false);
        }

        public static IFormFile GetIFormFile(string name, IFormFileCollection files)
        {
            if (files is null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            return files[name];
        }

        public static bool IsBackupFile(string name)
        {
            if (name is null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            var ext = Path.GetExtension(name);
            return ext.Equals(DataIdentificators.BackupExtension, StringComparison.OrdinalIgnoreCase);
        }
    }
}
