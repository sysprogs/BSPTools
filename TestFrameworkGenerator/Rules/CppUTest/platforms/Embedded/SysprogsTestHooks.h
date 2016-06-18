#pragma once

enum TestMessageSeverity
{
    tmsInfo = 0,
    tmsWarning = 1,
    tmsError = 2,   //Will automatically flag test as failed
};

/*
    General Packet format: [1-byte length (0-254)] [body] for short packets
                            [0xff] [4-byte length] [body] for long packets
    Body starts with packet type (1 byte) that is included in the reported length
*/

enum TestPacketType
{
    strpTestStartingByID   = 1,       //Arguments: 32-bit or 64-bit ID
    strpTestStartingByName,         //Arguments: test name
    strpTestEnded,                  //Arguments: none
    strpOutputMessage,              //Arguments: type, text
    strpTestFailed,                 //Arguments: a sequence of test objects prefixed by 1-byte TestObjectType
};

enum TestObjectType
{
    totErrorSummary,    //null-terminated message
    totErrorDetails,    //same
    totCodeLocation,    //<null-terminated file><line:32 bits>
    totCallStack        //<frame count: 8 bits> <array of file/line pairs>
};

extern "C"
{
    void __attribute__((noinline)) SysprogsTestHook_SelectTests(int testCount, void **pTests);
    void __attribute__((noinline)) SysprogsTestHook_TestStarting(void *pTest);
    void __attribute__((noinline)) SysprogsTestHook_TestEnded();
    void __attribute__((noinline)) SysprogsTestHook_OutputMessage(TestMessageSeverity severity, const char *pMessage);
    void __attribute__((noinline)) SysprogsTestHook_TestFailed(void *pTest, const char *pSummary, const char *pDetails);
    void __attribute__((noinline)) SysprogsTestHook_TestsCompleted();
}