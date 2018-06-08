#include <alloca.h>
#include <stdio.h>
#include <stdarg.h>
#include "TinyEmbeddedTest.h"
#include "SysprogsTestHooks.h"

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
    
    TestInstance **pAllInstances = (TestInstance **)alloca(testCount * sizeof(TestInstance *));
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
    char *pBuffer = (char *)alloca(requiredLength + 1);
    vsnprintf(pBuffer, requiredLength + 1, pFormat, ap);
    SysprogsTestHook_TestFailed(0, pBuffer, 0);
    va_end(ap);
	
#if TINY_EMBEDDED_TEST_CONTEXT_RESTORE_MODE == 1
	longjmp(s_TinyEmbeddedTestJumpBuffer, 1);
#elif TINY_EMBEDDED_TEST_CONTEXT_RESTORE_MODE == 2
	throw std::exception();
#endif
}
