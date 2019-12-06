#include "SysprogsTestHooks.h"
#include <FastSemihosting.h>
#include <string.h>

#ifdef SYSPROGS_TEST_PLATFORM_EMBEDDED

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

#ifdef ANDROID
#include <string.h>
#include <unistd.h>
#include <stddef.h>
#include <sys/socket.h>
#include <sys/un.h>

void __attribute__((noinline)) SysprogsTestHook_ReportingSocketReady()
{
}

const char * __attribute__((noinline)) SysprogsTestHook_QueryPipeName()
{
    static char pipeName[1024];
    return pipeName;
}

static int WaitForConnectionOnLocalSocket(const char* name)
{
    struct sockaddr_un sockAddr;
    
    int nameLen = strlen(name);
    if (nameLen >= (int) sizeof(sockAddr.sun_path) - 1)
        return -1;
    
    sockAddr.sun_path[0] = '\0';  /* abstract namespace */
    strcpy(sockAddr.sun_path + 1, name);
    sockAddr.sun_family = AF_LOCAL;
    socklen_t socklen = 1 + nameLen + offsetof(struct sockaddr_un, sun_path);
    
    int fd = socket(AF_LOCAL, SOCK_STREAM, PF_UNIX);
    if (fd < 0) 
        return -1;
    
    if (bind(fd, (const struct sockaddr*) &sockAddr, socklen) < 0) 
    {
        close(fd);
        return -1;
    }
    
    if (listen(fd, 5) < 0) 
    {
        close(fd);
        return -1;
    }
    
    SysprogsTestHook_ReportingSocketReady();
    int clientSock = accept(fd, NULL, NULL);
    close(fd);

    return clientSock;
}
#endif

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
     
        const char *pPipe = getenv("SYSPROGS_TEST_REPORTING_PIPE");
        if (pPipe && pPipe[0])
        {
            m_Pipe = open(pPipe, O_WRONLY);
        }
#ifdef ANDROID
        else if ((pPipe = getenv("SYSPROGS_TEST_REPORTING_SOCKET")) != NULL)
        {
            m_Pipe = WaitForConnectionOnLocalSocket(pPipe);
        }
        else if ((pPipe = SysprogsTestHook_QueryPipeName()) != NULL)
        {
            m_Pipe = WaitForConnectionOnLocalSocket(pPipe);
        }
#endif
        else
        {
            fprintf(stderr, "SYSPROGS_TEST_REPORTING_PIPE not set. Cannot report test status!\n");
            return;
        }
        
    }
    
    ~TestOutputPipe()
    {
        if (m_Pipe > 0)
            close(m_Pipe);
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

#ifdef ANDROID
extern "C" int __attribute__((weak)) main()
{
    return 0;
}
#else
extern "C" int main();
#endif

void __attribute__((noinline)) SysprogsTestHook_TestStarting(void *pTest)
{
	static bool s_ReferenceAddressReported = false;
    TestOutputSynchronizer sync;
	if (!s_ReferenceAddressReported)
	{
		s_ReferenceAddressReported = true;
		void *pMain = (void *)&main;
		unsigned char hdr2[] = { 1 + sizeof(pMain), strpReferenceAddressReport };
		WriteTestOutput(&hdr2, sizeof(hdr2), &pMain, sizeof(pMain));
	}
    unsigned char hdr[] = { 1 + sizeof(pTest), strpTestStartingByID };
    WriteTestOutput(&hdr, sizeof(hdr), &pTest, sizeof(pTest));
}

void __attribute__((noinline)) SysprogsTestHook_TestStartingEx(const char *pFullyQualifiedName)
{
    TestOutputSynchronizer sync;
    int nameLength = strlen(pFullyQualifiedName);
    unsigned char type = strpTestStartingByName;
    WriteTestPacketSize(nameLength + 1, &type, 1);
    WriteTestOutput(pFullyQualifiedName, nameLength, 0, 0);
}

void __attribute__((noinline)) SysprogsTestHook_TestEnded()
{
    TestOutputSynchronizer sync;
    
    const char hdr[] = { 1, strpTestEnded };
    WriteTestOutput(&hdr, sizeof(hdr), 0, 0);
}

void __attribute__((noinline)) SysprogsTestHook_OutputMessage(TestMessageSeverity severity, const char *pMessage)
{
	SysprogsTestHook_OutputMessageEx(severity, pMessage, pMessage ? strlen(pMessage) : 0);
}

void __attribute__((noinline)) SysprogsTestHook_OutputMessageEx(TestMessageSeverity severity, const char *pMessage, int length)
{
    TestOutputSynchronizer sync;
    
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

static volatile int s_IsRunningUnitTests;

int __attribute__((noinline)) IsRunningUnitTests()
{
	InitializeFastSemihosting(); //VisualGDB will set s_IsRunningUnitTests in response to this call.
	return s_IsRunningUnitTests;
}

void __attribute__((noinline)) SysprogsTestHook_TestsCompleted()
{
    asm("nop");
}
