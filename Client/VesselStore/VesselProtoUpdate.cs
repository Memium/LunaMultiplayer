﻿using LunaClient.Systems.SafetyBubble;
using LunaClient.Systems.VesselRemoveSys;
using LunaClient.Utilities;
using LunaClient.VesselUtilities;
using System;
using System.Collections.Concurrent;

namespace LunaClient.VesselStore
{
    public class VesselProtoUpdate
    {
        #region Fields & properties

        public Guid VesselId { get; }
        public bool VesselHasUpdate { get; set; }
        public bool ForceReload { get; set; }
        public bool HasInvalidParts { get; set; }

        private ProtoVessel _deserializedProtoVessel;
        public ProtoVessel ProtoVessel
        {
            get
            {
                if (_needToDeserializeData || _deserializedProtoVessel == null)
                {
                    DeserializeVesselBytes();
                }
                return _deserializedProtoVessel;
            }
        }

        public Vessel Vessel => FlightGlobals.FindVessel(VesselId);
        public bool VesselExist => Vessel != null;
        public bool ShouldBeLoaded => !HasInvalidParts;
        public bool IsInSafetyBubble => SafetyBubbleSystem.Singleton.IsInSafetyBubble(ProtoVessel);

        private readonly ConcurrentDictionary<uint, ProtoPartSnapshot> _vesselParts = new ConcurrentDictionary<uint, ProtoPartSnapshot>();
        public ConcurrentDictionary<uint, ProtoPartSnapshot> VesselParts
        {
            get
            {
                if (_needToDeserializeData)
                {
                    DeserializeVesselBytes();
                }
                return _vesselParts;
            }
        }

        #region Private

        private byte[] _vesselData = new byte[1000];
        private int _numBytes;
        private ConfigNode _vesselNode;
        private bool _needToDeserializeData = true;
        private readonly object _vesselDataSyncLock = new object();

        #endregion

        #endregion

        public VesselProtoUpdate(byte[] vesselData, int numBytes, Guid vesselId)
        {
            lock (_vesselDataSyncLock)
            {
                if (vesselId == Guid.Empty)
                    throw new Exception("Cannot create a VesselProtoUpdate with an empty vesselId.");

                VesselId = vesselId;
                CopyVesselBytes(vesselData, numBytes);
            }
        }
        
        /// <summary>
        /// Update this class with the new data received
        /// </summary>
        public void Update(byte[] vesselData, int numBytes, Guid vesselId, bool forceReload, bool reloadLocally)
        {
            lock (_vesselDataSyncLock)
            {
                if (VesselId != vesselId)
                {
                    LunaLog.LogError("Cannot update a VesselProtoUpdate with a different vesselId");
                    return;
                }

                CopyVesselBytes(vesselData, numBytes);

                _needToDeserializeData = true;
                VesselHasUpdate = reloadLocally;
                ForceReload = forceReload;
            }
        }

        /// <summary>
        /// Copies the received vessel bytes to this class
        /// </summary>
        private void CopyVesselBytes(byte[] vesselData, int numBytes)
        {
            _numBytes = numBytes;

            if (_vesselData.Length < _numBytes)
                Array.Resize(ref _vesselData, _numBytes);
            Array.Copy(vesselData, _vesselData, _numBytes);
        }

        /// <summary>
        /// This method uses a lot of memory so try to call it as less as possible and only when needed
        /// </summary>
        public void DeserializeVesselBytes()
        {
            lock (_vesselDataSyncLock)
            {
                _needToDeserializeData = false;
                var newVesselNode = ConfigNodeSerializer.Deserialize(_vesselData, _numBytes);
                if (!VesselCommon.VesselHasNaNPosition(newVesselNode))
                {                
                    //In case there's a deserialization error skip it and keep the older node
                    _vesselNode = newVesselNode;
                }
                if (_vesselNode == null)
                {
                    LunaLog.LogError($"Received a malformed vessel from SERVER. Id {VesselId}");
                    VesselRemoveSystem.Singleton.KillVessel(VesselId, "Malformed vessel");
                    VesselRemoveSystem.Singleton.AddToKillList(VesselId, "Malformed vessel");
                    return;
                }

                var newProto = VesselSerializer.CreateSafeProtoVesselFromConfigNode(_vesselNode, VesselId);

                //In case there's a deserialization error skip it and keep the older proto
                if (newProto != null)
                {
                    HasInvalidParts = VesselCommon.ProtoVesselHasInvalidParts(newProto);

                    if (newProto.vesselID != VesselId)
                    {
                        LunaLog.LogError($"Tried to update the Vessel with a proto from a different vessel ID. Proto: {newProto.vesselID} CorrectId: {VesselId}");
                    }
                    else
                    {
                        _deserializedProtoVessel = newProto;
                    }
                }

                //If protovessel is still null then unfortunately we must remove that vessel as the server sent us a bad vessel
                if (_deserializedProtoVessel == null)
                {
                    LunaLog.LogError($"Received a malformed vessel from SERVER. Id {VesselId}");
                    VesselRemoveSystem.Singleton.KillVessel(VesselId, "Malformed vessel");
                    VesselRemoveSystem.Singleton.AddToKillList(VesselId, "Malformed vessel");
                }
                else
                {
                    _vesselParts.Clear();
                    foreach (var protoPart in _deserializedProtoVessel.protoPartSnapshots)
                    {
                        _vesselParts.TryAdd(protoPart.flightID, protoPart);
                    }
                }
            }
        }
    }
}
