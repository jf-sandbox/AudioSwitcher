﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using AudioSwitcher.AudioApi.CoreAudio.Interfaces;
using AudioSwitcher.AudioApi.CoreAudio.Threading;

namespace AudioSwitcher.AudioApi.CoreAudio
{
    /// <summary>
    ///     Enumerates Windows System Devices.
    ///     Stores the current devices in memory to avoid calling the COM library when not required
    /// </summary>
    public sealed class CoreAudioController : AudioController<CoreAudioDevice>, ISystemAudioEventClient
    {
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        private IMultimediaDeviceEnumerator _innerEnumerator;
        private HashSet<CoreAudioDevice> _deviceCache = new HashSet<CoreAudioDevice>();

        public CoreAudioController()
        {
            ComThread.Invoke(() =>
            {
                // ReSharper disable once SuspiciousTypeConversion.Global
                _innerEnumerator = ComObjectFactory.GetDeviceEnumerator();

                if (_innerEnumerator == null)
                    return;

                _notificationClient = new MultimediaNotificationClient(this);
                _innerEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
            });

            RefreshSystemDevices();
        }

        ~CoreAudioController()
        {
            Dispose(false);
        }

        protected override void Dispose(bool disposing)
        {
            ComThread.BeginInvoke(() =>
            {
                if (_innerEnumerator != null)
                {
                    _innerEnumerator.UnregisterEndpointNotificationCallback(_notificationClient);
                    _notificationClient = null;
                    _innerEnumerator = null;
                }
            })
            .ContinueWith(x =>
            {
                if (_deviceCache != null)
                    _deviceCache.Clear();

                if (_lock != null)
                    _lock.Dispose();
            });

            base.Dispose(disposing);

            GC.SuppressFinalize(this);
        }

        public override CoreAudioDevice GetDevice(Guid id, DeviceState state)
        {
            var acquiredLock = _lock.AcquireReadLockNonReEntrant();

            try
            {
                return _deviceCache.FirstOrDefault(x => x.Id == id && state.HasFlag(x.State));
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitReadLock();
            }
        }

        private CoreAudioDevice GetDevice(string realId)
        {
            var acquiredLock = _lock.AcquireReadLockNonReEntrant();

            try
            {
                return
                    _deviceCache.FirstOrDefault(
                        x => string.Equals(x.RealId, realId, StringComparison.InvariantCultureIgnoreCase));
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitReadLock();
            }
        }

        private void RefreshSystemDevices()
        {
            ComThread.Invoke(() =>
            {
                _deviceCache = new HashSet<CoreAudioDevice>();
                IMultimediaDeviceCollection collection;
                _innerEnumerator.EnumAudioEndpoints(EDataFlow.All, EDeviceState.All, out collection);

                using (var coll = new MultimediaDeviceCollection(collection))
                {
                    foreach (var mDev in coll)
                        CacheDevice(mDev);
                }
            });
        }

        private CoreAudioDevice GetOrAddDeviceFromRealId(string deviceId)
        {
            //This pre-check here may prevent more com objects from being created
            var device = GetDevice(deviceId);
            if (device != null)
                return device;

            return ComThread.Invoke(() =>
            {
                IMultimediaDevice mDevice;
                _innerEnumerator.GetDevice(deviceId, out mDevice);

                if (mDevice == null)
                    return null;

                return CacheDevice(mDevice);
            });
        }

        private IEnumerable<CoreAudioDevice> RemoveFromRealId(string deviceId)
        {
            var lockAcquired = _lock.AcquireWriteLockNonReEntrant();
            try
            {
                var devicesToRemove =
                    _deviceCache.Where(
                        x => string.Equals(x.RealId, deviceId, StringComparison.InvariantCultureIgnoreCase)).ToList();

                _deviceCache.RemoveWhere(
                    x => string.Equals(x.RealId, deviceId, StringComparison.InvariantCultureIgnoreCase));

                return devicesToRemove;
            }
            finally
            {
                if (lockAcquired)
                    _lock.ExitWriteLock();
            }
        }

        private CoreAudioDevice CacheDevice(IMultimediaDevice mDevice)
        {
            if (!DeviceIsValid(mDevice))
                return null;

            string id;
            mDevice.GetId(out id);
            var device = GetDevice(id);

            if (device != null)
                return device;

            var lockAcquired = _lock.AcquireWriteLockNonReEntrant();

            try
            {
                device = new CoreAudioDevice(mDevice, this);
                _deviceCache.Add(device);

                return device;
            }
            finally
            {
                if (lockAcquired)
                    _lock.ExitWriteLock();
            }
        }

        private static bool DeviceIsValid(IMultimediaDevice device)
        {
            try
            {
                string id;
                EDeviceState state;
                device.GetId(out id);
                device.GetState(out state);

                return true;
            }
            catch
            {
                return false;
            }
        }

        public override bool SetDefaultDevice(CoreAudioDevice dev)
        {
            if (dev == null)
                return false;

            var oldDefault = dev.IsPlaybackDevice ? DefaultPlaybackDevice : DefaultCaptureDevice;

            try
            {
                PolicyConfig.SetDefaultEndpoint(dev.RealId, ERole.Console | ERole.Multimedia);
                return dev.IsDefaultDevice;
            }
            catch
            {
                return false;
            }
            finally
            {
                //Raise the default changed event on the old device
                if (oldDefault != null && !oldDefault.IsDefaultDevice)
                    OnAudioDeviceChanged(new DefaultDeviceChangedArgs(oldDefault));
            }
        }

        public override bool SetDefaultCommunicationsDevice(CoreAudioDevice dev)
        {
            if (dev == null)
                return false;

            var oldDefault = dev.IsPlaybackDevice
                ? DefaultPlaybackCommunicationsDevice
                : DefaultCaptureCommunicationsDevice;

            try
            {
                PolicyConfig.SetDefaultEndpoint(dev.RealId, ERole.Communications);

                return dev.IsDefaultCommunicationsDevice;
            }
            catch
            {
                return false;
            }
            finally
            {
                //Raise the default changed event on the old device
                if (oldDefault != null && !oldDefault.IsDefaultCommunicationsDevice)
                    OnAudioDeviceChanged(new DefaultDeviceChangedArgs(oldDefault));
            }
        }

        public override CoreAudioDevice GetDefaultDevice(DeviceType deviceType, Role role)
        {
            var acquiredLock = _lock.AcquireReadLockNonReEntrant();

            try
            {
                IMultimediaDevice dev;
                _innerEnumerator.GetDefaultAudioEndpoint(deviceType.AsEDataFlow(), role.AsERole(), out dev);
                if (dev == null)
                    return null;

                string devId;
                dev.GetId(out devId);
                if (string.IsNullOrEmpty(devId))
                    return null;

                return _deviceCache.FirstOrDefault(x => x.RealId == devId);
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitReadLock();
            }
        }

        public override IEnumerable<CoreAudioDevice> GetDevices(DeviceType deviceType, DeviceState state)
        {
            var acquiredLock = _lock.AcquireReadLockNonReEntrant();

            try
            {
                return _deviceCache.Where(x =>
                    (x.DeviceType == deviceType || deviceType == DeviceType.All)
                    && state.HasFlag(x.State)).ToList();
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitReadLock();
            }
        }

        private MultimediaNotificationClient _notificationClient;

        void ISystemAudioEventClient.OnDeviceStateChanged(string deviceId, EDeviceState newState)
        {
            var dev = GetOrAddDeviceFromRealId(deviceId);

            if (dev != null)
                OnAudioDeviceChanged(new DeviceStateChangedArgs(dev, newState.AsDeviceState()));
        }

        void ISystemAudioEventClient.OnDeviceAdded(string deviceId)
        {
            var dev = GetOrAddDeviceFromRealId(deviceId);

            if (dev != null)
                OnAudioDeviceChanged(new DeviceAddedArgs(dev));
        }

        void ISystemAudioEventClient.OnDeviceRemoved(string deviceId)
        {
            var devicesRemoved = RemoveFromRealId(deviceId);

            foreach (var dev in devicesRemoved)
                OnAudioDeviceChanged(new DeviceRemovedArgs(dev));
        }

        void ISystemAudioEventClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, string deviceId)
        {
            //Ignore multimedia, it seems to fire a console event anyway
            if (role == ERole.Multimedia)
                return;

            var dev = GetOrAddDeviceFromRealId(deviceId);

            if (dev == null)
                return;

            OnAudioDeviceChanged(new DefaultDeviceChangedArgs(dev));
        }

        private static readonly Dictionary<PropertyKey, Expression<Func<IDevice, object>>> PropertykeyToLambdaMap = new Dictionary
            <PropertyKey, Expression<Func<IDevice, object>>>
        {
            {PropertyKeys.PKEY_DEVICE_INTERFACE_FRIENDLY_NAME, x => x.InterfaceName},
            {PropertyKeys.PKEY_DEVICE_DESCRIPTION, x => x.Name},
            {PropertyKeys.PKEY_DEVICE_FRIENDLY_NAME, x => x.FullName},
            {PropertyKeys.PKEY_DEVICE_ICON, x => x.Icon},
        };

        void ISystemAudioEventClient.OnPropertyValueChanged(string deviceId, PropertyKey key)
        {
            var dev = GetOrAddDeviceFromRealId(deviceId);

            if (dev == null)
                return;

            //Ignore the properties we don't care about
            if (!PropertykeyToLambdaMap.ContainsKey(key))
                return;

            OnAudioDeviceChanged(DevicePropertyChangedArgs.FromExpression(dev, PropertykeyToLambdaMap[key]));
        }
    }
}