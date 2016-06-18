#include "SysprogsTestHooks.h"
#include <SysprogsProfilerInterface.h>
#include <string.h>

static void WriteTestOutput(const void *pHeader, unsigned headerSize, const void *pPayload, unsigned payloadSize)
{
    while (!SysprogsProfiler_WriteData(pdcTestReportStream, pHeader, headerSize, pPayload, payloadSize))
    {
        asm("nop");
    }
}

static void WriteTestPacketSize(unsigned size, const void *pExtraData, unsigned extraDataSize)
{
    if (size >= 255)
    {
        unsigned char hdr[] = { 0xFF, (unsigned char)(size), (unsigned char)(size >> 8), (unsigned char)(size >> 16), (unsigned char)(size >> 24)};
        WriteTestOutput(&hdr, sizeof(hdr), pExtraData, extraDataSize);
    }
    else
    {
        unsigned char hdr[] = { (unsigned char)size };
        WriteTestOutput(&hdr, sizeof(hdr), pExtraData, extraDataSize);
    }
}

void __attribute__((noinline)) SysprogsTestHook_SelectTests(int testCount, void **pTests)
{
    asm("nop");
}


void __attribute__((noinline)) SysprogsTestHook_TestStarting(void *pTest)
{
    unsigned char hdr[] = { 5, strpTestStartingByID };
    WriteTestOutput(&hdr, sizeof(hdr), &pTest, sizeof(pTest));
}


void __attribute__((noinline)) SysprogsTestHook_TestEnded()
{
    const char hdr[] = { 1, strpTestEnded };
    WriteTestOutput(&hdr, sizeof(hdr), 0, 0);
}


void __attribute__((noinline)) SysprogsTestHook_OutputMessage(TestMessageSeverity severity, const char *pMessage)
{
    int length = pMessage ? strlen(pMessage) : 0;
    const unsigned char hdr[] = { strpOutputMessage, (unsigned char)severity };
    WriteTestPacketSize(length + sizeof(hdr) + 1, &hdr, sizeof(hdr));
    WriteTestOutput(pMessage, length, "", 1);
}


void __attribute__((noinline)) SysprogsTestHook_TestFailed(void *pTest, const char *pSummary, const char *pDetails)
{
    int summaryLen = pSummary ? strlen(pSummary) + 2 : 0;
    int detailsLen = pDetails ? strlen(pDetails) + 2 : 0;
    const unsigned char hdr[] = { strpTestFailed };
    WriteTestPacketSize(sizeof(hdr) + summaryLen + detailsLen, &hdr, sizeof(hdr));
    unsigned char ch;
    if (pSummary)
    {
        ch = totErrorSummary;   
        WriteTestOutput(&ch, 1, 0, 0);
        WriteTestOutput(pSummary, summaryLen - 2, "", 1);
    }
    if (pDetails)
    {
        ch = totErrorDetails;   
        WriteTestOutput(&ch, 1, 0, 0);
        WriteTestOutput(pDetails, detailsLen - 2, "", 1);
    }
}

void __attribute__((noinline)) SysprogsTestHook_TestsCompleted()
{
    asm("nop");
}
