#pragma once
#include <string.h>
#include <math.h>

class TestGroup;

class TestInstance
{
public:
    TestInstance *m_pNextTestInGroup;
    TestGroup *m_pGroup;
    
    virtual void run() = 0;
};

class TestGroup
{
public:
    static TestGroup *s_pFirstTestGroup;
    TestGroup *m_pNextGroup;
    TestInstance *m_pFirstTest;
        
    virtual void setup() {}
    virtual void teardown() {}
    
    void Register()
    {
        m_pNextGroup = s_pFirstTestGroup;
        s_pFirstTestGroup = this;
    }
    
    void RegisterTest(class TestInstance *pTest)
    {
        if (pTest->m_pGroup)
            return; //Already registered
        pTest->m_pNextTestInGroup = m_pFirstTest;
        m_pFirstTest = pTest;
        pTest->m_pGroup = this;
    }
};

template <typename _TestGroup> static inline TestGroup *GetTestGroup()
{
    static _TestGroup testGroup;
    static bool registered = false;
    if (!registered)
    {
        registered = true;
        testGroup.Register();
    }
    return &testGroup;
}

#define TEST_GROUP(groupName) struct TestGroup_ ## groupName; \
    struct TestGroup_ ## groupName : public TestGroup
    
#define TEST(groupName, testName) class TestInstance_ ## groupName ## _ ## testName : public TestInstance \
{ \
public: \
    void run(); \
}; \
TestInstance_ ## groupName ## _ ## testName testInstance_ ## groupName ## _ ## testName; \
void __attribute__((constructor)) RegisterTest_ ## groupName ## _ ## testName () \
{ \
    GetTestGroup<TestGroup_ ## groupName>()->RegisterTest(&testInstance_ ## groupName ## _ ## testName); \
} \
void TestInstance_ ## groupName ## _ ## testName::run()
        
void RunAllTests();
void ReportTestFailure(const char *pFormat, ...);

#define CHECK(condition) ((condition) || (ReportTestFailure("Unexpected boolean value: expected true, found false"), 0))
#define CHECK_FALSE(condition) ((!condition) || (ReportTestFailure("Unexpected boolean value: expected false, found true"), 0))
#define CHECK_EQUAL(expected, actual) ((expected == actual) || (ReportTestFailure("Unexpected value in CHECK_EQUAL()"), 0))

#define STRCMP_EQUAL(expected, actual) ((!strcmp((expected), (actual))) || (ReportTestFailure("Unexpected string value: expected %s, found %s", (expected), (actual)), 0))
#define STRNCMP_EQUAL(expected, actual, length) ((!strncmp((expected), (actual), (length))) || (ReportTestFailure("Unexpected string value: expected %s, found %s", (expected), (actual)), 0))

#define STRCMP_NOCASE_EQUAL(expected, actual) ((!strcasecmp((expected), (actual))) || (ReportTestFailure("Unexpected string value: expected %s, found %s", (expected), (actual)), 0))
#define STRCMP_CONTAINS(expected, actual) ((strstr((actual), (expected))) || (ReportTestFailure("Unexpected string value: %s does not contain %s", (actual), (expected)), 0))

#define LONGS_EQUAL(expected, actual) (((long)(expected) == (long)(actual)) || (ReportTestFailure("Unexpected long value: expected %ld, found %ld", (expected), (actual)), 0))
#define UNSIGNED_LONGS_EQUAL(expected, actual) (((unsigned long)(expected) == (unsigned long)(actual)) || (ReportTestFailure("Unexpected unsigned long value: expected %uld, found %uld", (expected), (actual)), 0))
#define UNSIGNED_LONGS_EQUAL_WITHIN(expected, actual, tolerance) (( (((unsigned long)(expected) < (unsigned long)(actual)) ? ((unsigned long)(actual) - (unsigned long)(expected)) : ((unsigned long)(expected) - (unsigned long)(actual))) <= (unsigned long)(tolerance)) || (ReportTestFailure("Unexpected unsigned long value: expected %uld, found %uld", (expected), (actual)), 0))

#define BYTES_EQUAL(expected, actual) (((unsigned char)(expected) == (unsigned char)(actual)) || (ReportTestFailure("Unexpected byte value: expected %d, found %d", (unsigned char)(expected), (unsigned char)(actual)), 0))
#define POINTERS_EQUAL(expected, actual) (((void *)(expected) == (void *)(actual)) || (ReportTestFailure("Unexpected pointer value: expected %p, found %p", (void *)(expected), (void *)(actual)), 0))

#define DOUBLES_EQUAL(expected, actual, tolerance) ((fabs((actual)-(expected)) <= (tolerance)) || (ReportTestFailure("Unexpected double value: expected %lf, found %lf", (double)expected, (double)(actual), 0), 0))
#define DOUBLES_NOT_EQUAL(expected, actual, tolerance) ((fabs((double)(actual)-(double)(expected)) > (double)(tolerance)) || (ReportTestFailure("Unexpected double value: expected not equal to %f, found %f", (double)(expected), (double)(actual)), 0))
#define MEMCMP_EQUAL(expected, actual, size) ((!memcmp((expected), (actual), (size))) || (ReportTestFailure("Unexpected memory block contents"), 0))

#define FLOATS_EQUAL(expected, actual, tolerance) ((fabsf((float)(actual)-(float)(expected)) <= (float)(tolerance)) || (ReportTestFailure("Unexpected float value: expected %f, found %f", (float)(expected), (float)(actual)), 0))
#define FLOATS_NOT_EQUAL(expected, actual, tolerance) ((fabsf((float)(actual)-(float)(expected)) > (float)(tolerance)) || (ReportTestFailure("Unexpected float value: expected not equal to %f, found %f", (float)(expected), (float)(actual)), 0))

#define BITS_EQUAL(expected, actual, mask) ((((expected) & (mask)) == ((actual) & (mask))) || (ReportTestFailure("Unexpected value: expected %x, found %x", (expected) & (mask), (actual) & (mask)), 0))
#define FAIL(text) ReportTestFailure(text)
