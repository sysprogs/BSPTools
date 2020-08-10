#include "TinyEmbeddedTest.h"

#ifdef SIMULATION

#include <crtdbg.h>
#include <set>
#include <stdio.h>
#include <string>
#include <windows.h>

namespace TinyEmbeddedTest
{
	void InitializeSimulation(int testCount, TestInstance **pTests)
	{
		LPSTR pCommandLine = GetCommandLine();
		if (strstr(pCommandLine, "/listtests"))
		{
			printf("Available tests:\n");
			for (TestGroup *pGroup = TestGroup::s_pFirstTestGroup; pGroup; pGroup = pGroup->m_pNextGroup)
			{
				for (TestInstance *pTest = pGroup->m_pFirstTest; pTest; pTest = pTest->m_pNextTestInGroup)
				{
					printf("group = '%s', test = '%s', file = '', line = '', address = '%08x', attributes = '", pGroup->GetName(), pTest->GetName(), pTest);

					const char *pTag = pTest->GetTag();
					size_t tagLength = 0;
					if (pTag)
					{
						const char marker[] = "attachAttributes(TestAttributeCollection<";
						pTag = strstr(pTag, marker);
						if (pTag)
							pTag += sizeof(marker) - 1;
						char *p2 = strchr(pTag, '>');
						if (p2)
							tagLength = p2 - pTag;
					}

					if (pTag && tagLength)
						fwrite(pTag, tagLength, 1, stdout);

					printf("'\n");
				}
			}

			fflush(stdout);
			ExitProcess(0);
		}
		else if (strstr(pCommandLine, "/runtests "))
		{
			const char *p = strstr(pCommandLine, "/runtests ") + 10;
			std::set<std::string> selectedTests;

			for (const char *pNext = strchr(p, ' '); p; p = pNext ? (pNext + 1) : NULL, pNext = p ? strchr(p, ' ') : NULL)
			{
				std::string str;

				if (pNext)
					str = std::string(p, pNext - p);
				else
					str = p;

				if (str != "" && str != " ")
					selectedTests.insert(str);
			}

			int deleted = 0;

			if (selectedTests.size() > 0)
			{
				for (int i = 0; i < testCount; i++)
				{
					std::string name = std::string(pTests[i]->m_pGroup->GetName()) + "." + pTests[i]->GetName();
					if (selectedTests.find(name) == selectedTests.end())
					{
						deleted++;
						continue;
					}

					pTests[i - deleted] = pTests[i];
				}
			}

			for (int i = testCount - deleted; i < testCount; i++)
				pTests[i] = NULL;
		}
	}

	bool IsRunningUnitTestsInSimulation()
	{
		LPSTR pCommandLine = GetCommandLine();
		return strstr(pCommandLine, "/listtests") || strstr(pCommandLine, "/runtests");
	}
} // namespace TinyEmbeddedTest

size_t GetNumberOfWin32AllocatedBytes()
{
	_HEAPINFO info = {
		0,
	};

	size_t allocated = 0;

	for (;;)
	{
		int ret = _heapwalk(&info);
		if (ret == _HEAPEND || !info._pentry)
			break;

		if (info._useflag == _USEDENTRY)
			allocated += info._size;
	}

	return allocated;
}

#endif
