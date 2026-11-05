#import <UIKit/UIKit.h>
#import <NodeMobile/NodeMobile.h>
#include <sys/socket.h>
#include <arpa/inet.h>
#include <unistd.h>
#include <errno.h>
#include <poll.h>

// Diagnostic host only. No user scripts execute in UIKit or the Node host realm.
static BOOL transfer(int fd, void *bytes, size_t count, BOOL writing) {
    size_t offset = 0;
    while (offset < count) {
        ssize_t result = writing ? write(fd, bytes + offset, count - offset)
                                 : read(fd, bytes + offset, count - offset);
        if (result < 0 && errno == EINTR) continue;
        if (result < 0 && (errno == EAGAIN || errno == EWOULDBLOCK)) {
            struct pollfd waiting = {.fd = fd, .events = writing ? POLLOUT : POLLIN};
            int ready;
            do { ready = poll(&waiting, 1, -1); } while (ready < 0 && errno == EINTR);
            if (ready > 0) continue;
        }
        if (result <= 0) return NO;
        offset += (size_t)result;
    }
    return YES;
}

static BOOL sendJSON(int fd, NSDictionary *value) {
    NSData *data = [NSJSONSerialization dataWithJSONObject:value options:0 error:NULL];
    uint32_t length = htonl((uint32_t)data.length);
    return transfer(fd, &length, sizeof(length), YES)
        && transfer(fd, (void *)data.bytes, data.length, YES);
}

static void nativePeer(int fd, NSString *report) {
    @autoreleasepool {
        BOOL described = NO, echoed = NO;
        NSDictionary *pendingEcho = nil;
        NSMutableArray *messages = [NSMutableArray array];
        BOOL sent = sendJSON(fd, @{@"version": @1, @"session": @"ios-preflight", @"kind": @"request",
                      @"id": @1, @"method": @"runtime.describe", @"params": NSNull.null});
        uint32_t header;
        while (transfer(fd, &header, sizeof(header), NO)) {
            uint32_t size = ntohl(header);
            if (!size || size > 1024 * 1024) break;
            NSMutableData *data = [NSMutableData dataWithLength:size];
            if (!transfer(fd, data.mutableBytes, size, NO)) break;
            NSDictionary *message = [NSJSONSerialization JSONObjectWithData:data options:0 error:NULL];
            if (![message isKindOfClass:NSDictionary.class]) break;
            [messages addObject:message];
            if ([message[@"kind"] isEqual:@"result"]) {
                described = [message[@"id"] isEqual:@1]
                    && [message[@"value"][@"product"] isEqual:@"ios-preflight"];
            } else if ([message[@"kind"] isEqual:@"request"]
                       && [message[@"method"] isEqual:@"native.echo"]) {
                echoed = [message[@"params"][@"text"] isEqual:@"iOS→Node→iOS"];
                pendingEcho = message;
            } else break;
            if (described && pendingEcho) {
                if (!sendJSON(fd, @{@"version": @1, @"session": @"ios-preflight",
                                   @"kind": @"result", @"id": pendingEcho[@"id"],
                                   @"value": pendingEcho[@"params"]})) break;
                pendingEcho = nil;
            }
        }
        close(fd);
        NSDictionary *result = @{@"described": @(described), @"echoed": @(echoed),
                                 @"closed": @YES, @"sent": @(sent), @"messages": messages};
        [[NSJSONSerialization dataWithJSONObject:result options:NSJSONWritingPrettyPrinted error:NULL]
            writeToFile:report atomically:YES];
    }
}

@interface AppDelegate : UIResponder <UIApplicationDelegate>
@property(nonatomic, strong) UIWindow *window;
@end
@implementation AppDelegate
- (BOOL)application:(UIApplication *)application didFinishLaunchingWithOptions:(NSDictionary *)options {
    self.window = [[UIWindow alloc] initWithFrame:UIScreen.mainScreen.bounds];
    self.window.rootViewController = [UIViewController new];
    self.window.rootViewController.view.backgroundColor = UIColor.systemBackgroundColor;
    [self.window makeKeyAndVisible];
    NSString *root = [NSBundle.mainBundle.resourcePath stringByAppendingPathComponent:@"Runtime"];
    NSString *documents = NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES).firstObject;
    int sockets[2];
    if (socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) != 0) return NO;
    int nativeFD = sockets[0], nodeFD = sockets[1];
    int noSignal = 1;
    setsockopt(nativeFD, SOL_SOCKET, SO_NOSIGPIPE, &noSignal, sizeof(noSignal));
    NSThread *peer = [[NSThread alloc] initWithBlock:^{
        nativePeer(nativeFD, [documents stringByAppendingPathComponent:@"native-peer.json"]);
    }];
    peer.name = @"preflight.native-peer";
    [peer start];
    NSThread *runtime = [[NSThread alloc] initWithBlock:^{
        @autoreleasepool {
            NSArray<NSString *> *arguments = @[@"node",
                [root stringByAppendingPathComponent:@"probe.mjs"],
                documents, [NSString stringWithFormat:@"%d", nodeFD]];
            // node_start requires writable, contiguous argument storage.
            size_t length = 0;
            for (NSString *argument in arguments) length += strlen(argument.UTF8String) + 1;
            char *storage = malloc(length), *cursor = storage;
            char **argv = calloc(arguments.count + 1, sizeof(char *));
            for (NSUInteger i = 0; i < arguments.count; i++) {
                argv[i] = cursor;
                strcpy(cursor, arguments[i].UTF8String);
                cursor += strlen(cursor) + 1;
            }
            int code = node_start((int)arguments.count, argv);
            free(argv);
            free(storage);
            [@{@"exitCode": @(code)} writeToFile:
                [documents stringByAppendingPathComponent:@"node-return.plist"] atomically:YES];
        }
    }];
    runtime.name = @"preflight.node";
    runtime.stackSize = 8 * 1024 * 1024;
    [runtime start];
    return YES;
}
@end

int main(int argc, char **argv) {
    @autoreleasepool { return UIApplicationMain(argc, argv, nil, NSStringFromClass(AppDelegate.class)); }
}
