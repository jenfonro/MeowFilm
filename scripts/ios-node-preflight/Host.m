#import <UIKit/UIKit.h>
#import <NodeMobile/NodeMobile.h>
#include <stdlib.h>
#include <string.h>

// Diagnostic host; the real App lifecycle is covered by hosted RuntimeTests.
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
    NSThread *runtime = [[NSThread alloc] initWithBlock:^{
        @autoreleasepool {
            NSMutableArray<NSString *> *arguments = [NSMutableArray arrayWithArray:@[@"node",
                [root stringByAppendingPathComponent:@"probe.mjs"],
                documents]];
            NSArray<NSString *> *launch = NSProcessInfo.processInfo.arguments;
            [arguments addObjectsFromArray:[launch subarrayWithRange:NSMakeRange(1, launch.count - 1)]];
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
