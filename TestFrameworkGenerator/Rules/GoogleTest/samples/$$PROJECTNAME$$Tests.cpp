#include <gtest/gtest.h>
#include <stdio.h>

/*
	This is a very basic sample demonstrating the GoogleTest framework.
	Read more about CppUTest syntax here: https://github.com/google/googletest
*/

TEST(DemoTestGroup, FailingTest)
{
    EXPECT_EQ(1, 1);
    EXPECT_EQ(1, 2);	//<= This test should fail here
}

TEST(DemoTestGroup, SuccessfulTest1)
{
	//This test should succeed
    EXPECT_EQ(1, 1);
}

TEST(DemoTestGroup, SuccessfulTest2)
{
	//This test should succeed;
	printf("Hello from Test #2");
}
