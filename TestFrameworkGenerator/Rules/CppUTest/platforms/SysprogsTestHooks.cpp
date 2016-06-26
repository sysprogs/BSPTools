#include "SysprogsTestHooks.h"
#include <string.h>

#ifdef CPPUTEST_BAREBONE

#include <SysprogsProfilerInterface.h>

static void WriteTestOutput(const void *pHeader, unsigned headerSize, const void *pPayload, unsigned payloadSize)
{
    while (!SysprogsProfiler_WriteData(pdcTestReportStream, pHeader, headerSize, pPayload, payloadSize))
    {
        asm("nop");
    }
}

class TestOutputSynchronizer
{
    //Nothing here, not needed on Embedded
};

#else

#include <pthread.h>
#include <fcntl.h>
#include <stdlib.h>
#include <stdio.h>
#include <unistd.h>

class TestOutputPipe
{
private:
    int m_Pipe;
    pthread_mutex_t m_Mutex;
    
public:
    TestOutputPipe()
    {
        m_Pipe = -1;
        pthread_mutex_init(&m_Mutex, 0);
     
        char *pPipe = getenv("SYSPROGS_TEST_REPORTING_PIPE");
        if (!pPipe || !pPipe[0])
        {
            fprintf(stderr, "SYSPROGS_TEST_REPORTING_PIPE not set. Cannot report test status!\n");
            return;
        }
        
        m_Pipe = open(pPipe, O_WRONLY);
    }
    
    
    void WriteTestOutput(const void *pData, unsigned dataSize)
    {
        unsigned done = 0;
        while (done < dataSize)
        {
            int doneNow = write(m_Pipe, (char *)pData + done, dataSize - done);
            if (doneNow <= 0)
                break;
            done += doneNow;
        }
    }
    
    void Lock()
    {
        pthread_mutex_lock(&m_Mutex);
    }
    
    void Unlock()
    {
        pthread_mutex_unlock(&m_Mutex);
    }
};


TestOutputPipe g_Pipe;
unsigned g_SysprogsTestReportTimestamp;

class TestOutputSynchronizer
{
public:
    TestOutputSynchronizer()
    {
        g_Pipe.Lock();
    }
    
    ~TestOutputSynchronizer()
    {
        unsigned timestamp = g_SysprogsTestReportTimestamp + 1;
        unsigned char hdr[] = { 5, strpTimestamp, (unsigned char)(timestamp), (unsigned char)(timestamp >> 8), (unsigned char)(timestamp >> 16), (unsigned char)(timestamp >> 24) };
        g_Pipe.WriteTestOutput(&hdr, sizeof(hdr));
        g_SysprogsTestReportTimestamp = timestamp;
        g_Pipe.Unlock();
    }
};

static void WriteTestOutput(const void *pHeader, unsigned headerSize, const void *pPayload, unsigned payloadSize)
{
    g_Pipe.WriteTestOutput(pHeader, headerSize);
    g_Pipe.WriteTestOutput(pPayload, payloadSize);
}


#endif

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
    TestOutputSynchronizer sync;
    unsigned char hdr[] = { 1 + sizeof(pTest), strpTestStartingByID };
    WriteTestOutput(&hdr, sizeof(hdr), &pTest, sizeof(pTest));
}


void __attribute__((noinline)) SysprogsTestHook_TestEnded()
{
    TestOutputSynchronizer sync;
    
    const char hdr[] = { 1, strpTestEnded };
    WriteTestOutput(&hdr, sizeof(hdr), 0, 0);
}


void __attribute__((noinline)) SysprogsTestHook_OutputMessage(TestMessageSeverity severity, const char *pMessage)
{
    TestOutputSynchronizer sync;
    
    int length = pMessage ? strlen(pMessage) : 0;
    const unsigned char hdr[] = { strpOutputMessage, (unsigned char)severity };
    WriteTestPacketSize(length + sizeof(hdr) + 1, &hdr, sizeof(hdr));
    WriteTestOutput(pMessage, length, "", 1);
}


void __attribute__((noinline)) SysprogsTestHook_TestFailed(void *pTest, const char *pSummary, const char *pDetails)
{
    TestOutputSynchronizer sync;
    
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
