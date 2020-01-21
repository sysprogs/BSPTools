#include <stdio.h>
#include <stdarg.h>
#include "TinyEmbeddedTest.h"
#include "SysprogsTestHooks.h"

#ifndef __ICCARM__
#include <alloca.h>
#endif

#ifndef TINY_EMBEDDED_TEST_CONTEXT_RESTORE_MODE
#define TINY_EMBEDDED_TEST_CONTEXT_RESTORE_MODE 0
#endif

TestGroup *TestGroup::s_pFirstTestGroup = 0;

#if TINY_EMBEDDED_TEST_CONTEXT_RESTORE_MODE == 1
static jmp_buf s_TinyEmbeddedTestJumpBuffer;
#elif TINY_EMBEDDED_TEST_CONTEXT_RESTORE_MODE == 2
#include <exception>
#endif

void RunAllTests()
{
    int testCount = 0;
    for (TestGroup *pGroup = TestGroup::s_pFirstTestGroup; pGroup; pGroup = pGroup->m_pNextGroup)
        for (TestInstance *pTest = pGroup->m_pFirstTest; pTest; pTest = pTest->m_pNextTestInGroup)
            testCount++;

#ifdef __ICCARM__
	TestInstance **pAllInstances = (TestInstance **)malloc(testCount * sizeof(TestInstance *));	//WARNING: this will cause a one-time memory leak
#else
	TestInstance **pAllInstances = (TestInstance **)alloca(testCount * sizeof(TestInstance *));
#endif
    int index = 0;
    
    for (TestGroup *pGroup = TestGroup::s_pFirstTestGroup; pGroup; pGroup = pGroup->m_pNextGroup)
        for (TestInstance *pTest = pGroup->m_pFirstTest; pTest; pTest = pTest->m_pNextTestInGroup)
            pAllInstances[index++] = pTest;
    
    SysprogsTestHook_SelectTests(testCount, (void **)pAllInstances);
    
    TestGroup *pGroup = 0;
    
    for (index = testCount - 1; index >= 0; index--)
    {
        TestInstance *pInstance = pAllInstances[index];
        if (!pInstance)
            continue;
        if (pInstance->m_pGroup != pGroup)
        {
            if (pGroup)
                pGroup->teardown();
            pGroup = pInstance->m_pGroup;
            if (pGroup)
                pGroup->setup();
        }
        
	    SysprogsTestHook_TestStarting(pInstance);
	    
	    pGroup->TestSetup(pInstance);
	    
#if TINY_EMBEDDED_TEST_CONTEXT_RESTORE_MODE == 1
	    if (!setjmp(s_TinyEmbeddedTestJumpBuffer))
	    {
		    pInstance->run();
	    }
#elif TINY_EMBEDDED_TEST_CONTEXT_RESTORE_MODE == 2
	    try
	    {
		    pInstance->run();
	    }
	    catch (...)
	    {
	    }
#else
	    pInstance->run();
#endif
	    
	    pGroup->TestTeardown(pInstance);
	    
        SysprogsTestHook_TestEnded();
    }
    
    if (pGroup)
        pGroup->teardown();
    
    SysprogsTestHook_TestsCompleted();
}


void ReportTestFailure(const char *pFormat, ...)
{
    va_list ap;
    va_start(ap, pFormat);
    int requiredLength = vsnprintf(0, 0, pFormat, ap);
	
#ifdef __ICCARM__
	char *pBuffer = (char *)malloc(requiredLength + 1);
#else
    char *pBuffer = (char *)alloca(requiredLength + 1);
#endif
	
    vsnprintf(pBuffer, requiredLength + 1, pFormat, ap);
    SysprogsTestHook_TestFailed(0, pBuffer, 0);
    va_end(ap);

#ifdef __ICCARM__
	free(pBuffer);
#endif

#if TINY_EMBEDDED_TEST_CONTEXT_RESTORE_MODE == 1
	longjmp(s_TinyEmbeddedTestJumpBuffer, 1);
#elif TINY_EMBEDDED_TEST_CONTEXT_RESTORE_MODE == 2
	throw std::exception();
#endif
}


void OutputTestMessage(const char *pMessage)
{
	SysprogsTestHook_OutputMessage(tmsInfo, pMessage);
}

#ifndef TINY_EMBEDDED_TEST_HOOK_STDIO
#define TINY_EMBEDDED_TEST_HOOK_STDIO 0
#endif

#if TINY_EMBEDDED_TEST_HOOK_STDIO
int _isatty()
{
	return 1;
}

#ifdef __IAR_SYSTEMS_ICC__
extern "C" size_t __write(int fd, const unsigned char *pBuffer, size_t size)
#else
extern "C" int _write(int fd, char *pBuffer, int size)
#endif
{
	SysprogsTestHook_OutputMessageEx(tmsInfo, pBuffer, size);
	//If we return less than [size], the newlib will retry the write call for the remaining bytes.
	//However if we are running the the non-blocking mode, we actually want to skip the extra data to avoid slowdown.
	return size;
}
#endif