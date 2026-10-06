//
// Created by Tim Schneeberger on 16.05.21.
// Copyright (c) 2021 Tim Schneeberger. Licensed under GPLv3.
//

#include <stdlib.h>
#include <string.h>

#import <IOBluetooth/IOBluetooth.h>

#import "Bluetooth.h"
#import "NativeStringUtils.h"

static void FreeEnumerationDevices(EnumerationResult *result, int initializedCount) {
    if (result == NULL || result->devices == NULL) {
        return;
    }

    for (int i = 0; i < initializedCount; i++) {
        free(result->devices[i].mac_address);
        free(result->devices[i].device_name);
    }

    free(result->devices);
    result->devices = NULL;
    result->length = 0;
}

typedef void (^BtConnectFinish)(BT_CONN_RESULT result);

@interface Bluetooth ()
@property(nonatomic, copy) BtConnectFinish connectFinish;
@property(nonatomic, copy) NSString *pendingMac;
@property(nonatomic, strong) NSData *pendingUuid;
@property(nonatomic, assign) NSInteger connectGeneration;
@property(nonatomic, assign) BOOL continuedAfterSdp;
/// After phone/other host used SPP, Mac often keeps a stale ACL that rejects RFCOMM.
/// Set on RFCOMM failure so the next attempt opens a fresh baseband link.
@property(nonatomic, assign) BOOL bounceBasebandBeforeNextConnect;
@end

@implementation Bluetooth {
    NSString *_macAddress;
    Bt_OnChannelData _onChannelData;
    Bt_OnChannelClosed _onChannelClosed;
    BOOL _channelClosedSignalled;
}

- (id)init {
    if (self = [super init]) {
        _macAddress = NULL;
        _connectGeneration = 0;
    }

    return self;
}

- (void)finishConnect:(BT_CONN_RESULT)result generation:(NSInteger)generation
{
    if (generation != self.connectGeneration) {
        return;
    }

    BtConnectFinish finish = self.connectFinish;
    self.connectFinish = nil;
    self.pendingMac = nil;
    self.pendingUuid = nil;

    if (finish) {
        finish(result);
    }
}

// Never dispatch_sync onto the main queue: openRFCOMMChannelSync / openConnection can
// block for a minute and freeze the UI, or deadlock with .NET waiting on main.
// Instead: schedule work async on main (so Avalonia keeps pumping), wait on a semaphore.
- (BT_CONN_RESULT)connect:(NSString *)mac uuid:(const UInt8 *)uuid {
    __block BT_CONN_RESULT result = BT_CONN_EUNKNOWN;
    dispatch_semaphore_t done = dispatch_semaphore_create(0);
    NSData *uuidData = [NSData dataWithBytes:uuid length:16];

    dispatch_async(dispatch_get_main_queue(), ^{
        [self beginConnectOnMain:mac uuid:uuidData finish:^(BT_CONN_RESULT r) {
            result = r;
            dispatch_semaphore_signal(done);
        }];
    });

    const int64_t timeoutNs = (int64_t)12 * NSEC_PER_SEC;
    if (dispatch_semaphore_wait(done, dispatch_time(DISPATCH_TIME_NOW, timeoutNs)) != 0) {
        NSLog(@"Error: RFCOMM connect timed out after 12s\n");
        NSInteger generation = self.connectGeneration;
        dispatch_async(dispatch_get_main_queue(), ^{
            if (generation != self.connectGeneration) {
                return;
            }
            // Invalidate so a late open-complete cannot leave a zombie channel.
            self.connectGeneration++;
            [self forceCloseRfcommChannel];
            self.bounceBasebandBeforeNextConnect = YES;
            IOBluetoothDevice *device = nil;
            if ([Bluetooth getDevice:mac result:&device] && [device isConnected]) {
                NSLog(@"Warning: Requesting baseband close for %@ after connect timeout\n", mac);
                [device closeConnection];
            }
            BtConnectFinish finish = self.connectFinish;
            self.connectFinish = nil;
            self.pendingMac = nil;
            self.pendingUuid = nil;
            if (finish) {
                finish(BT_CONN_EOPEN);
            }
        });
        // Allow the main-queue cleanup to run before the next attempt.
        [NSThread sleepForTimeInterval:0.5];
        return BT_CONN_EOPEN;
    }

    return result;
}

- (void)beginConnectOnMain:(NSString *)mac
                       uuid:(NSData *)uuidData
                     finish:(BtConnectFinish)finish
{
    NSInteger generation = ++self.connectGeneration;
    self.connectFinish = finish;
    self.pendingMac = mac;
    self.pendingUuid = uuidData;
    self.continuedAfterSdp = NO;

    if (mRFCOMMChannel != nil) {
        NSLog(@"Warning: Cleaning up existing RFCOMM channel before connect\n");
        [self forceCloseRfcommChannel];
    }

    IOBluetoothDevice *device = NULL;
    if (![Bluetooth getDevice:mac result:&device]) {
        [self finishConnect:BT_CONN_ENOTPAIRED generation:generation];
        return;
    }

    // After Samsung Wearable (or a failed RFCOMM open) the existing ACL often cannot
    // host a new GEARMANAGER channel until it is torn down and reopened.
    if (self.bounceBasebandBeforeNextConnect) {
        self.bounceBasebandBeforeNextConnect = NO;
        [self bounceBasebandForMac:mac];
        // Re-resolve in case IOBluetooth replaced the object.
        if (![Bluetooth getDevice:mac result:&device]) {
            [self finishConnect:BT_CONN_ENOTPAIRED generation:generation];
            return;
        }
    }

    if (![device isConnected]) {
        NSLog(@"Warning: Device baseband not connected; attempting openConnection\n");
        IOReturn status = [device openConnection];
        if (status == kIOReturnTimeout) {
            [self finishConnect:BT_CONN_ENOTFOUND generation:generation];
            return;
        }
        if (status != kIOReturnSuccess) {
            NSLog(@"Error: %s opening connection to device.\n", mach_error_string(status));
            [self finishConnect:BT_CONN_EBASECONN generation:generation];
            return;
        }
    }

    sdpQueryDone = NO;
    // sdp query with uuids specified silently fails since Ventura, ref https://developer.apple.com/forums/thread/722228
    IOReturn status = [device performSDPQuery:self];
    if (status != kIOReturnSuccess) {
        NSLog(@"Error: %s starting SDP query.\n", mach_error_string(status));
        // Fall through and try cached records immediately.
        [self continueConnectAfterSdp:device generation:generation];
        return;
    }

    // Soft timeout for SDP: use cached records if the callback is slow.
    __weak Bluetooth *weakSelf = self;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(3.0 * NSEC_PER_SEC)),
                   dispatch_get_main_queue(), ^{
        Bluetooth *strongSelf = weakSelf;
        if (!strongSelf || generation != strongSelf.connectGeneration || strongSelf->sdpQueryDone) {
            return;
        }
        NSLog(@"Warning: SDP query timed out.\n");
        strongSelf->sdpQueryDone = YES;
        [strongSelf continueConnectAfterSdp:device generation:generation];
    });
}

- (void)continueConnectAfterSdp:(IOBluetoothDevice *)device generation:(NSInteger)generation
{
    if (generation != self.connectGeneration || self.connectFinish == nil) {
        return;
    }
    if (self.continuedAfterSdp) {
        return;
    }
    self.continuedAfterSdp = YES;

    const UInt8 *uuidBytes = (const UInt8 *)self.pendingUuid.bytes;
    IOBluetoothSDPUUID *parsedUuid = [IOBluetoothSDPUUID uuidWithBytes:uuidBytes length:16];
    IOBluetoothSDPServiceRecord *serviceRecord = [device getServiceRecordForUUID:parsedUuid];

    if (serviceRecord == nil) {
        NSLog(@"Error - service in selected device. ***This should never happen.***");
        return BT_CONN_ESDP;
    }

    UInt8 rfcommChannelID;
    IOReturn status = [serviceRecord getRFCOMMChannelID:&rfcommChannelID];
    if (status != kIOReturnSuccess) {
        NSLog(@"Error: %s getting RFCOMM channel ID from service.\n", mach_error_string(status));
        [self finishConnect:BT_CONN_ECID generation:generation];
        return;
    }

    NSLog(@"Service selected '%@' - RFCOMM Channel ID = %u\n", [serviceRecord getServiceName], rfcommChannelID);

    // Open the RFCOMM channel on the new device connection
    IOBluetoothRFCOMMChannel *tempRFCOMMChannel = mRFCOMMChannel;
    status = [device openRFCOMMChannelSync:&tempRFCOMMChannel withChannelID:rfcommChannelID delegate:self];
    @synchronized (self) {
        mRFCOMMChannel = tempRFCOMMChannel;
        _channelClosedSignalled = NO;
    }

    if (mRFCOMMChannel == nil) {
        NSLog(@"Error: %s - unable to open RFCOMM channel.\n", mach_error_string(status) );
        [self disconnect];
        return BT_CONN_EOPEN;
    }

    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:4.0];
    while ([device isConnected] && [deadline timeIntervalSinceNow] > 0) {
        [[NSRunLoop currentRunLoop] runMode:NSDefaultRunLoopMode
                                 beforeDate:[NSDate dateWithTimeIntervalSinceNow:0.05]];
    }

    if ([device isConnected]) {
        NSLog(@"Warning: Baseband still connected after bounce wait\n");
    } else {
        NSLog(@"Baseband closed; opening a fresh ACL for RFCOMM\n");
    }
}

- (void)sdpQueryComplete:(IOBluetoothDevice *)device status:(IOReturn)status {
    if (status != kIOReturnSuccess) {
        NSLog(@"Error: %s performing SDP query.\n", mach_error_string(status));
    }

    if (sdpQueryDone) {
        // Soft timeout already continued.
        return;
    }
    sdpQueryDone = YES;

    NSInteger generation = self.connectGeneration;
    [self continueConnectAfterSdp:device generation:generation];
}

- (void)rfcommChannelOpenComplete:(IOBluetoothRFCOMMChannel *)rfcommChannel status:(IOReturn)status
{
    NSInteger generation = self.connectGeneration;
    if (generation == 0 || self.connectFinish == nil) {
        return;
    }

    if (status != kIOReturnSuccess || rfcommChannel == nil || ![rfcommChannel isOpen]) {
        NSLog(@"Error: %s - unable to open RFCOMM channel.\n", mach_error_string(status));
        [self failRfcommOpen:generation];
        return;
    }

    mRFCOMMChannel = rfcommChannel;
    _macAddress = [[NSString alloc] initWithString:self.pendingMac ?: @""];
    NSLog(@"RFCOMM channel open complete\n");
    [self finishConnect:BT_CONN_SUCCESS generation:generation];
}

- (void)forceCloseRfcommChannel
{
    // mRFCOMMChannel is also read by sendData on another thread; swap it out
    // under the lock so in-flight sends keep a valid (closed) channel object.
    IOBluetoothRFCOMMChannel *channel;
    @synchronized (self) {
        channel = mRFCOMMChannel;
        mRFCOMMChannel = nil;
    }

    if (channel != nil) {
        // This will close the RFCOMM channel and start an inactivity timer to close the baseband connection if no
        // other channels (L2CAP or RFCOMM) are open.
        [channel setDelegate:nil];
        [channel closeChannel];
    }

    _macAddress = NULL;
}

- (void)requestBasebandBounceForNextConnect:(NSString *)mac
{
    // Only arm the flag — the actual close happens once inside beginConnect.
    // Closing here caused: close → macOS reconnect → OnConnected → close again.
    self.bounceBasebandBeforeNextConnect = YES;
    NSLog(@"RFCOMM reconnect will bounce baseband ACL for %@ once\n", mac ?: @"(unknown)");
}

- (BOOL)disconnect
{
    // Invalidate any in-flight async connect so its callbacks become no-ops.
    self.connectGeneration++;
    BtConnectFinish finish = self.connectFinish;
    self.connectFinish = nil;
    self.pendingMac = nil;
    self.pendingUuid = nil;

    void (^doClose)(void) = ^{
        [self forceCloseRfcommChannel];
    };

    if ([NSThread isMainThread]) {
        doClose();
    } else {
        // Timed wait — never dispatch_sync(main); main may be busy with IOBluetooth.
        dispatch_semaphore_t closed = dispatch_semaphore_create(0);
        dispatch_async(dispatch_get_main_queue(), ^{
            doClose();
            dispatch_semaphore_signal(closed);
        });
        dispatch_semaphore_wait(closed, dispatch_time(DISPATCH_TIME_NOW, (int64_t)2 * NSEC_PER_SEC));
    }

    if (finish) {
        finish(BT_CONN_EOPEN);
    }

    return TRUE;
}

// A remote close (rfcommChannelClosed:) can race an in-flight send hitting the closed
// channel, and both paths would otherwise fire _onChannelClosed -> a duplicate managed
// Disconnected event. Signal at most once per connection; reset on the next connect.
- (void)signalChannelClosedOnce {
    @synchronized (self) {
        if (_channelClosedSignalled) {
            return;
        }
        _channelClosedSignalled = YES;
    }

    if (_onChannelClosed) {
        _onChannelClosed();
    }
}

- (BOOL)isConnected {
    return mRFCOMMChannel != nil && [mRFCOMMChannel isOpen];
}

- (BT_ENUM_RESULT)enumerate:(EnumerationResult *)result {
    if (result == NULL) {
        NSLog(@"Error - failed to enumerate - result pointer is null!");
        return BT_ENUM_EUNKNOWN;
    }

    result->length = 0;
    result->devices = NULL;

    NSArray *inDevices = [IOBluetoothDevice pairedDevices];

    int deviceCount = (int)(unsigned long)[inDevices count];
    if (deviceCount == 0) {
        return BT_ENUM_SUCCESS;
    }

    result->devices = (Device*)calloc((size_t)deviceCount, sizeof(Device));
    if (!result->devices) {
        NSLog(@"Error - failed to enumerate - out of memory!");
        return BT_ENUM_EUNKNOWN;
    }

    int validCount = 0;
    for (int i = 0; i < deviceCount; i++) {
        IOBluetoothDevice *device = [inDevices objectAtIndex:i];
        NSString *addressString = [device addressString];
        if (IsNullOrEmpty(addressString)) {
            NSLog(@"Bluetooth::enumerate(): Skipping paired device with missing address: %@", device);
            continue;
        }

        Device *resultDevice = &result->devices[validCount];
        NSString *nameString = FirstNonEmptyString([device name], [device nameOrAddress], addressString);

        resultDevice->device_name = CopyNSStringToUtf8CString(nameString);
        resultDevice->mac_address = CopyNSStringToUtf8CString(addressString);
        if (resultDevice->device_name == NULL || resultDevice->mac_address == NULL) {
            NSLog(@"Error - failed to enumerate - unable to copy device strings!");
            FreeEnumerationDevices(result, validCount + 1);
            return BT_ENUM_EUNKNOWN;
        }

        resultDevice->is_connected = device.isConnected;
        resultDevice->is_paired = device.isPaired;
        resultDevice->cod = device.classOfDevice;
        validCount++;
    }

    result->length = validCount;
    return BT_ENUM_SUCCESS;
}

- (BT_SEND_RESULT)sendData:(char *)buffer length:(UInt32)length
{
    // Snapshot the channel: the IOBluetooth callback thread can run disconnect
    // (nilling mRFCOMMChannel) while this loop is mid-write. The local strong
    // reference keeps the object alive; writeSync on a closed channel just
    // returns an error and ends the loop.
    IOBluetoothRFCOMMChannel *channel;
    @synchronized (self) {
        channel = mRFCOMMChannel;
    }

    if (channel != nil) {
        if (![channel isOpen]) {
            // Only tear down if this snapshot is still the current channel; a concurrent
            // disconnect/reconnect may have already replaced it, and disconnecting here
            // would kill the fresh connection or re-report an intentional teardown.
            BOOL isCurrent;
            @synchronized (self) {
                isCurrent = (channel == mRFCOMMChannel);
            }
            if (isCurrent) {
                [self disconnect];
                [self signalChannelClosedOnce];
            }
            return BT_SEND_ENULL;
        }
        UInt32 numBytesRemaining;
        IOReturn result;
        BluetoothRFCOMMMTU rfcommChannelMTU;

        numBytesRemaining = length;
        result = kIOReturnSuccess;

        // Get the RFCOMM Channel's MTU.  Each write can only contain up to the MTU size
        // number of bytes.
        rfcommChannelMTU = [channel getMTU];

        // Loop through the data until we have no more to send.
        while ( (result == kIOReturnSuccess) && (numBytesRemaining > 0) ) {
            if (![channel isOpen]) {
                result = kIOReturnNotOpen;
                break;
            }

            // finds how many bytes I can send:
            UInt32 numBytesToSend = ( (numBytesRemaining > rfcommChannelMTU) ? rfcommChannelMTU : numBytesRemaining);

            // This method won't return until the buffer has been passed to the Bluetooth hardware to be sent to the remote device.
            // Alternatively, the asynchronous version of this method could be used which would queue up the buffer and return immediately.
            result = [channel writeSync:buffer length:static_cast<UInt16>(numBytesToSend)];

            // Updates the position in the buffer:
            numBytesRemaining -= numBytesToSend;
            buffer += numBytesToSend;
        }

        // We are successful only if all the data was sent:
        if ( (numBytesRemaining == 0) && (result == kIOReturnSuccess) ) {
            return BT_SEND_SUCCESS;
        } else if (result == kIOReturnSuccess) {
            return BT_SEND_EPARTIAL;
        }
    }

    return BT_SEND_EUNKNOWN;
}

- (void)setOnChannelData:(Bt_OnChannelData)callback {
    _onChannelData = callback;
}

- (void)setOnChannelClosed:(Bt_OnChannelClosed)callback {
    _onChannelClosed = callback;
}

- (NSString *)currentMac {
    return _macAddress;
}

+ (BOOL)getDevice:(NSString *)nsId result:(IOBluetoothDevice **)device {
    *device = [IOBluetoothDevice deviceWithAddressString:nsId];

    if (!*device) {
        NSLog(@"Bluetooth::getDevice(): Device not found by address: %@\n", nsId);
        return FALSE;
    }

    return TRUE;
}

- (void)rfcommChannelData:(IOBluetoothRFCOMMChannel *)rfcommChannel data:(void *)dataPointer length:(size_t)dataLength {
    if (_onChannelData) {
        _onChannelData(dataPointer, dataLength);
    }
}

- (void)rfcommChannelClosed:(IOBluetoothRFCOMMChannel *)rfcommChannel {
    [self disconnect];
    [self signalChannelClosedOnce];
}

@end
