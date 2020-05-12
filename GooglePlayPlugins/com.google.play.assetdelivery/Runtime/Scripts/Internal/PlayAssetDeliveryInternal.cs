// Copyright 2020 Google LLC
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     https://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Google.Play.Common;
using Google.Play.Core.Internal;
using UnityEngine;

namespace Google.Play.AssetDelivery.Internal
{
    /// <summary>
    /// Internal implementation of <see cref="PlayAssetDelivery"/> methods.
    /// </summary>
    internal class PlayAssetDeliveryInternal
    {
        /// <summary>
        /// The subfolder, inside the "assets" directory, into which an asset pack file is stored.
        /// This intermediate folder can be useful for targeting (by texture compression format) at build time.
        /// </summary>
        private const string AssetPackFolderName = "assetpack";

        private readonly AssetPackManager _assetPackManager;
        private readonly AssetPackStateUpdateListener _stateUpdateListener;
        private readonly AssetDeliveryUpdateHandler _updateHandler;

        private readonly PlayRequestRepository _requestRepository = new PlayRequestRepository();

        internal PlayAssetDeliveryInternal()
        {
            _assetPackManager = new AssetPackManager();
            _updateHandler = AssetDeliveryUpdateHandler.CreateInScene(_assetPackManager, _requestRepository);
            _updateHandler.OnStateUpdateEvent += ProcessPackStateUpdate;
            PlayCoreEventHandler.CreateInScene();
        }

        internal bool IsDownloaded(string assetBundleName)
        {
            return GetAssetLocation(assetBundleName) != null;
        }

        internal PlayAssetBundleRequest RetrieveAssetBundleAsyncInternal(string assetBundleName)
        {
            if (_requestRepository.ContainsRequest(assetBundleName))
            {
                throw new ArgumentException(string.Format("There is already an active request for AssetBundle: {0}",
                    assetBundleName));
            }

            var request = CreateAssetBundleRequest(assetBundleName);
            _requestRepository.AddRequest(request.PackRequest);
            request.Completed += req => _requestRepository.RemoveRequest(assetBundleName);

            InitiateRequest(request.PackRequest);

            return request;
        }

        internal PlayAssetPackRequest RetrieveAssetPackAsyncInternal(string assetPackName)
        {
            if (_requestRepository.ContainsRequest(assetPackName))
            {
                throw new ArgumentException(string.Format("There is already an active request for asset pack: {0}",
                    assetPackName));
            }

            var request = CreateAssetPackRequest(assetPackName);
            _requestRepository.AddRequest(request);
            request.Completed += req => _requestRepository.RemoveRequest(assetPackName);

            InitiateRequest(request);

            return request;
        }

        internal PlayAssetPackBatchRequest RetrieveAssetPackBatchAsyncInternal(IList<string> assetPackNames)
        {
            if (assetPackNames.Count != assetPackNames.Distinct().Count())
            {
                throw new ArgumentException("assetPackNames contains duplicate entries");
            }

            var activePackNames = assetPackNames.Where(name => _requestRepository.ContainsRequest(name)).ToArray();
            if (activePackNames.Length != 0)
            {
                throw new ArgumentException("There are already active requests for asset packs: {0}",
                    string.Join(", ", activePackNames));
            }

            var requests = new List<PlayAssetPackRequestImpl>();
            foreach (var assetPackName in assetPackNames)
            {
                var request = CreateAssetPackRequest(assetPackName);
                _requestRepository.AddRequest(request);
                request.Completed += req => _requestRepository.RemoveRequest(request.AssetPackName);
                requests.Add(request);
            }

            var batchRequest = new PlayAssetPackBatchRequestImpl(requests);
            var fetchTask = _assetPackManager.Fetch(assetPackNames.ToArray());
            fetchTask.RegisterOnSuccessCallback(javaPackStates =>
            {
                batchRequest.OnInitializedInPlayCore();
                fetchTask.Dispose();
            });
            fetchTask.RegisterOnFailureCallback((reason, errorCode) =>
            {
                Debug.LogErrorFormat("Failed to retrieve asset pack batch: {0}", reason);
                batchRequest.OnInitializationErrorOccurred(PlayCoreTranslator.TranslatePlayCoreErrorCode(errorCode));
                fetchTask.Dispose();
            });

            return batchRequest;
        }

        internal PlayAsyncOperation<ConfirmationDialogResult, AssetDeliveryErrorCode>
            ShowCellularDataConfirmationInternal()
        {
            var requests = _requestRepository.GetRequestsWithStatus(AssetDeliveryStatus.WaitingForWifi);
            if (requests.Count == 0)
            {
                throw new Exception("There are no active requests waiting for wifi.");
            }

            var task = _assetPackManager.ShowCellularDataConfirmation();
            var operation = new AssetDeliveryAsyncOperation<ConfirmationDialogResult>();
            task.RegisterOnSuccessCallback(resultCode =>
            {
                operation.SetResult(ConvertToConfirmationDialogResult(resultCode));
                task.Dispose();
            });
            task.RegisterOnFailureCallback((message, errorCode) =>
            {
                operation.SetError(PlayCoreTranslator.TranslatePlayCoreErrorCode(errorCode));
                task.Dispose();
            });
            return operation;
        }

        internal PlayAsyncOperation<long, AssetDeliveryErrorCode> GetDownloadSizeInternal(string assetBundleName)
        {
            var operation = new AssetDeliveryAsyncOperation<long>();

            if (IsInstallTimeAssetBundle(assetBundleName))
            {
                operation.SetResult(0L);
                return operation;
            }

            var task = _assetPackManager.GetPackStates(assetBundleName);
            task.RegisterOnSuccessCallback(javaPackState =>
            {
                var assetPacks = new AssetPackStates(javaPackState);
                operation.SetResult(assetPacks.TotalBytes);
                task.Dispose();
            });
            task.RegisterOnFailureCallback((message, errorCode) =>
            {
                operation.SetError(PlayCoreTranslator.TranslatePlayCoreErrorCode(errorCode));
                task.Dispose();
            });
            return operation;
        }

        internal PlayAsyncOperation<VoidResult, AssetDeliveryErrorCode> RemoveAssetPackInternal(
            string assetBundleName)
        {
            var operation = new AssetDeliveryAsyncOperation<VoidResult>();
            var task = _assetPackManager.RemovePack(assetBundleName);
            task.RegisterOnSuccessCallback(javaPackState =>
            {
                operation.SetResult(new VoidResult());
                task.Dispose();
            });
            task.RegisterOnFailureCallback((message, errorCode) =>
            {
                operation.SetError(AssetDeliveryErrorCode.InternalError);
                task.Dispose();
            });
            return operation;
        }

        private AssetLocation GetAssetLocation(string assetBundleName)
        {
            var assetPath = Path.Combine(AssetPackFolderName, assetBundleName);
            return _assetPackManager.GetAssetLocation(assetBundleName, assetPath);
        }

        private PlayAssetPackRequestImpl CreateAssetPackRequest(string assetPackName)
        {
            return new PlayAssetPackRequestImpl(assetPackName, _assetPackManager, _requestRepository);
        }

        private PlayAssetBundleRequestImpl CreateAssetBundleRequest(string assetBundleName)
        {
            var packRequest = CreateAssetPackRequest(assetBundleName);
            return new PlayAssetBundleRequestImpl(packRequest, _updateHandler);
        }

        private void InitiateRequest(PlayAssetPackRequestImpl request)
        {
            if (IsDownloaded(request.AssetPackName))
            {
                request.OnPackAvailable();
            }
            else
            {
                var fetchTask = _assetPackManager.Fetch(request.AssetPackName);
                fetchTask.RegisterOnSuccessCallback(javaPackStates =>
                {
                    request.OnInitializedInPlayCore();
                    fetchTask.Dispose();
                });
                fetchTask.RegisterOnFailureCallback((reason, errorCode) =>
                {
                    Debug.LogErrorFormat("Failed to retrieve asset pack: {0}", reason);
                    request.OnErrorOccured(PlayCoreTranslator.TranslatePlayCoreErrorCode(errorCode));
                    fetchTask.Dispose();
                });
            }
        }

        private void ProcessPackStateUpdate(AssetPackState newState)
        {
            PlayAssetPackRequestImpl request;
            if (!_requestRepository.TryGetRequest(newState.Name, out request))
            {
                Debug.LogWarningFormat(
                    "Received state update \"{0}\", that is not associated with an active request.",
                    newState.Name);
                return;
            }

            UpdateRequest(request, newState, newState.ErrorCode);
        }

        private void UpdateRequest(PlayAssetPackRequestImpl request, AssetPackState newState, int errorCode)
        {
            if (request.IsDone)
            {
                // Ignore pack state updates associated with completed requests.
                return;
            }

            var assetDeliveryErrorCode = PlayCoreTranslator.TranslatePlayCoreErrorCode(errorCode);
            if (assetDeliveryErrorCode != AssetDeliveryErrorCode.NoError)
            {
                request.OnErrorOccured(assetDeliveryErrorCode);
                return;
            }

            if (newState.Status == PlayCoreTranslator.AssetPackStatus.Canceled)
            {
                request.OnErrorOccured(AssetDeliveryErrorCode.Canceled);
                return;
            }

            request.UpdateState(PlayCoreTranslator.TranslatePlayCorePackStatus(newState.Status),
                newState.BytesDownloaded,
                newState.TotalBytesToDownload);
        }

        private bool IsInstallTimeAssetBundle(string assetBundleName)
        {
            var packLocation = _assetPackManager.GetPackLocation(assetBundleName);
            return packLocation != null && packLocation.PackStorageMethod == AssetPackStorageMethod.ApkAssets;
        }

        private ConfirmationDialogResult ConvertToConfirmationDialogResult(int resultCode)
        {
            ConfirmationDialogResult dialogResult;
            switch (resultCode)
            {
                case ActivityResult.ResultOk:
                    dialogResult = ConfirmationDialogResult.Accepted;
                    break;
                case ActivityResult.ResultCancelled:
                    dialogResult = ConfirmationDialogResult.Denied;
                    break;
                default:
                    throw new NotImplementedException("Unexpected activity result: " + resultCode);
            }

            return dialogResult;
        }
    }
}