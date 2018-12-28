﻿using System;
using System.Collections.Generic;
using System.Security;
using System.Xml.Linq;
using mRemoteNG.App;
using mRemoteNG.Config.Serializers.CredentialSerializer;
using mRemoteNG.Connection;
using mRemoteNG.Credential;
using mRemoteNG.Credential.Repositories;
using mRemoteNG.Security.Authentication;
using mRemoteNG.Security.Factories;
using mRemoteNG.Tools;
using mRemoteNG.Tree;
using System.Globalization;
using System.Linq;

namespace mRemoteNG.Config.Serializers.Versioning
{
    public class XmlCredentialManagerUpgrader : IDeserializer<string, ConnectionTreeModel>
    {
        private readonly CredentialServiceFacade _credentialsService;
        private readonly IDeserializer<string, ConnectionTreeModel> _decoratedDeserializer;

        public string CredentialFilePath { get; set; }
        

        public XmlCredentialManagerUpgrader(CredentialServiceFacade credentialsService, string credentialFilePath, IDeserializer<string, ConnectionTreeModel> decoratedDeserializer)
        {
            if (credentialsService == null)
                throw new ArgumentNullException(nameof(credentialsService));
            if (decoratedDeserializer == null)
                throw new ArgumentNullException(nameof(decoratedDeserializer));

            _credentialsService = credentialsService;
            CredentialFilePath = credentialFilePath;
            _decoratedDeserializer = decoratedDeserializer;
        }

        public ConnectionTreeModel Deserialize(string serializedData)
        {
            var serializedDataAsXDoc = EnsureConnectionXmlElementsHaveIds(serializedData);
            var upgradeMap = UpgradeUserFilesForCredentialManager(serializedDataAsXDoc);
            var serializedDataWithIds = $"{serializedDataAsXDoc.Declaration}{serializedDataAsXDoc}";

            var connectionTreeModel = _decoratedDeserializer.Deserialize(serializedDataWithIds);

            if (upgradeMap != null)
                ApplyCredentialMapping(upgradeMap, connectionTreeModel.GetRecursiveChildList());

            return connectionTreeModel;
        }

        private XDocument EnsureConnectionXmlElementsHaveIds(string serializedData)
        {
            var xdoc = XDocument.Parse(serializedData);
            xdoc.Declaration = new XDeclaration("1.0", "utf-8", null);
            var adapter = new ConfConsEnsureConnectionsHaveIds();
            adapter.EnsureElementsHaveIds(xdoc);
            return xdoc;
        }

        public static decimal? GetVersionFromConfiguration(XDocument xdoc)
        {
            var versionString = xdoc.Root?.Attribute("ConfVersion")?.Value;
            return versionString != null
                ? (decimal?)decimal.Parse(versionString, CultureInfo.InvariantCulture)
                : null;
        }

        public Dictionary<Guid, ICredentialRecord> UpgradeUserFilesForCredentialManager(XDocument xdoc)
        {
            if (!UpgradeNeeded(xdoc))
            {
                return null;
            }

            var cryptoProvider = new CryptoProviderFactoryFromXml(xdoc.Root).Build();
            var encryptedValue = xdoc.Root?.Attribute("Protected")?.Value;
            var auth = new PasswordAuthenticator(cryptoProvider, encryptedValue, () => MiscTools.PasswordDialog("", false));
            if (!auth.Authenticate(Runtime.EncryptionKey))
                throw new Exception("Could not authenticate");

            var newCredRepoKey = auth.LastAuthenticatedPassword;

            var credentialHarvester = new CredentialHarvester();
            var harvestedCredentials = credentialHarvester.Harvest(xdoc, newCredRepoKey);

            var newCredentialRepository = BuildXmlCredentialRepo(newCredRepoKey);

            AddHarvestedCredentialsToRepo(harvestedCredentials, newCredentialRepository);
            newCredentialRepository.SaveCredentials(newCredRepoKey);

            _credentialsService.AddRepository(newCredentialRepository);
            return credentialHarvester.ConnectionToCredentialMap;
        }

        /// <summary>
        /// If any connections in the xml contain a Username field, we need to upgrade
        /// </summary>
        /// <param name="xdoc"></param>
        /// <returns></returns>
        private bool UpgradeNeeded(XContainer xdoc)
        {
            return xdoc.Descendants("Node").Any(n => n.Attribute("Username") != null);
        }

        private ICredentialRepository BuildXmlCredentialRepo(SecureString newCredRepoKey)
        {
            var cryptoFromSettings = new CryptoProviderFactoryFromSettings();
            var credRepoSerializer = new XmlCredentialPasswordEncryptorDecorator(
                cryptoFromSettings.Build(),
                new XmlCredentialRecordSerializer());
            var credRepoDeserializer = new XmlCredentialPasswordDecryptorDecorator(new XmlCredentialRecordDeserializer());

            var xmlRepoFactory = new XmlCredentialRepositoryFactory(credRepoSerializer, credRepoDeserializer);
            var newRepo = xmlRepoFactory.Build(
                new CredentialRepositoryConfig
                {
                    Source = CredentialFilePath,
                    Title = "Converted Credentials",
                    TypeName = "Xml",
                    Key = newCredRepoKey
                }
            );
            newRepo.LoadCredentials(newCredRepoKey);
            return newRepo;
        }

        private void AddHarvestedCredentialsToRepo(IEnumerable<ICredentialRecord> harvestedCredentials, ICredentialRepository repo)
        {
            foreach (var credential in harvestedCredentials)
                repo.CredentialRecords.Add(credential);
        }

        public void ApplyCredentialMapping(IDictionary<Guid, ICredentialRecord> map, IEnumerable<AbstractConnectionRecord> connectionRecords)
        {
            foreach (var connectionInfo in connectionRecords)
            {
                Guid id;
                Guid.TryParse(connectionInfo.ConstantID, out id);
                if (map.ContainsKey(id))
                    connectionInfo.CredentialRecordId = map[id].Id.Maybe();
            }
        }
    }
}