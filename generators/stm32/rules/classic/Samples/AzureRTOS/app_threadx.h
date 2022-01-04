#pragma once

#include "tx_api.h"

#define APP_STACK_SIZE                       512
#define APP_BYTE_POOL_SIZE                   (2 * 1024)

#define THREAD_ONE_PRIO                      10
#define THREAD_ONE_PREEMPTION_THRESHOLD      THREAD_ONE_PRIO

#define THREAD_TWO_PRIO                      10
#define THREAD_TWO_PREEMPTION_THRESHOLD      THREAD_TWO_PRIO

#define DEFAULT_TIME_SLICE                   5
