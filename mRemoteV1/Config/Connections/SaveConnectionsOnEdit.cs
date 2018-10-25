﻿using mRemoteNG.Connection;
using System;
using System.Collections.Specialized;
using System.ComponentModel;

namespace mRemoteNG.Config.Connections
{
    public class SaveConnectionsOnEdit
    {
        private readonly IConnectionsService _connectionsService;

        public bool Enabled { get; set; } = true;

        public SaveConnectionsOnEdit(IConnectionsService connectionsService)
        {
            if (connectionsService == null)
                throw new ArgumentNullException(nameof(connectionsService));

            _connectionsService = connectionsService;
            connectionsService.ConnectionsLoaded += ConnectionsServiceOnConnectionsLoaded;
        }

        private void ConnectionsServiceOnConnectionsLoaded(object sender, ConnectionsLoadedEventArgs connectionsLoadedEventArgs)
        {
            connectionsLoadedEventArgs.NewConnectionTreeModel.CollectionChanged += ConnectionTreeModelOnCollectionChanged;
            connectionsLoadedEventArgs.NewConnectionTreeModel.PropertyChanged += ConnectionTreeModelOnPropertyChanged;

            foreach (var oldTree in connectionsLoadedEventArgs.PreviousConnectionTreeModel)
            {
                oldTree.CollectionChanged -= ConnectionTreeModelOnCollectionChanged;
                oldTree.PropertyChanged -= ConnectionTreeModelOnPropertyChanged;
            }
        }

        private void ConnectionTreeModelOnPropertyChanged(object sender, PropertyChangedEventArgs propertyChangedEventArgs)
        {
            SaveConnectionOnEdit();
        }

        private void ConnectionTreeModelOnCollectionChanged(object sender, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs)
        {
            SaveConnectionOnEdit();
        }

        private void SaveConnectionOnEdit()
        {
            if (!mRemoteNG.Settings.Default.SaveConnectionsAfterEveryEdit)
                return;

            if (!Enabled)
                return;

            _connectionsService.SaveConnectionsAsync();
        }
    }
}
