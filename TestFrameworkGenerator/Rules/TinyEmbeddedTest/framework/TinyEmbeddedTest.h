#pragma once
#include <string.h>
#include <math.h>

#if !defined(__ARMCC_VERSION) && !defined(__IAR_SYSTEMS_ICC__)
#include <strings.h>
#endif

#include <setjmp.h>

class TestGroup;

#ifdef SIMULATION
#define STORE_TEST_NAMES
#endif

class NamedTestObject
{
#ifdef STORE_TEST_NAMES
  private:
	const char *m_pName = NULL;
	const char *m_pTag = NULL;

  public:
	void AttachTag(const char *pTag) { m_pTag = pTag; }
	void AttachName(const char *pName) { m_pName = pName; }
	const char *GetName() { return m_pName; }
	const char *GetTag() { return m_pTag; }
#else
  public:
	void AttachName(const char *)
	{
	}

	const char *GetName() { return NULL; }
	const char *GetTag() { return NULL; }

#endif
};

class TestInstance : public NamedTestObject
{
public:
    TestInstance *m_pNextTestInGroup;
    TestGroup *m_pGroup;
    
    virtual void run() = 0;
};

namespace TinyEmbeddedTest
{
	void InitializeSimulation(int testCount, TestInstance **pTests);
}

class TestGroup : public NamedTestObject
{
  public:
    static TestGroup *s_pFirstTestGroup;
    TestGroup *m_pNextGroup;
    TestInstance *m_pFirstTest;
        
    virtual void setup() {}
    virtual void teardown() {}
	
	virtual void TestSetup(TestInstance *) {}
	virtual void TestTeardown(TestInstance *) {}
    
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

	//This is used in simulation environment where we need to report test names and select them by name.
	void RegisterTest(class TestInstance *pTest, const char *pGroupName, const char *pTestName)
	{
		RegisterTest(pTest);
		this->AttachName(pGroupName);
		pTest->AttachName(pTestName);
	}

	friend void TinyEmbeddedTest::InitializeSimulation(int testCount, TestInstance **pTests);
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

template <class Attr1 = void, class Attr2 = void, class Attr3 = void, class Attr4 = void, class Attr5 = void, class Attr6 = void, class Attr7 = void, class Attr8 = void>
class TestAttributeCollection
{
};

#ifdef STORE_TEST_NAMES
#define REGISTER_TEST_WRAPPER(method, groupName, testName) RegisterTest(method, groupName, testName)
#define DO_ATTACH_TEST_TAG(tag) AttachTag(tag)
#else
#define REGISTER_TEST_WRAPPER(method, groupName, testName) RegisterTest(method)
#define DO_ATTACH_TEST_TAG(tag)
#endif

#define TEST(groupName, testName) class TestInstance_ ## groupName ## _ ## testName : public TestInstance \
{ \
public: \
    void run(); \
}; \
TestInstance_ ## groupName ## _ ## testName testInstance_ ## groupName ## _ ## testName; \
void __attribute__((constructor)) RegisterTest_ ## groupName ## _ ## testName () \
{ \
    GetTestGroup<TestGroup_ ## groupName>()->REGISTER_TEST_WRAPPER(&testInstance_ ## groupName ## _ ## testName, #groupName, #testName); \
} \
void TestInstance_ ## groupName ## _ ## testName::run()
        
//The attachAttributes() method is intentionally blank and is only used during ELF symbol enumeration
#define TEST_WITH_ATTRIBUTES(groupName, testName, ...) class TestInstance_ ## groupName ## _ ## testName : public TestInstance \
{ \
public: \
	void __attribute__ ((noinline)) attachAttributes(TestAttributeCollection<__VA_ARGS__> * = nullptr) { DO_ATTACH_TEST_TAG(__PRETTY_FUNCTION__); } \
    void run(); \
	TestInstance_ ## groupName ## _ ## testName () { attachAttributes(); } \
}; \
TestInstance_ ## groupName ## _ ## testName testInstance_ ## groupName ## _ ## testName; \
void __attribute__((constructor)) RegisterTest_ ## groupName ## _ ## testName () \
{ \
    GetTestGroup<TestGroup_ ## groupName>()->REGISTER_TEST_WRAPPER(&testInstance_ ## groupName ## _ ## testName, #groupName, #testName); \
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

void OutputTestMessage(const char *pMessage);
