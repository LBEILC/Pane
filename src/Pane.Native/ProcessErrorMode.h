#pragma once
#include <windows.h>
#include <werapi.h>
#include <cstdio>
#include <cstdlib>
#include <exception>

namespace pane::diagnostics {
inline LONG WINAPI ReportFault(EXCEPTION_POINTERS* fault) {
    std::fprintf(stderr, "Unhandled SEH exception 0x%08lX\n", fault->ExceptionRecord->ExceptionCode);
    std::fflush(stderr);
    return EXCEPTION_EXECUTE_HANDLER;
}

inline void Initialize() {
    SetErrorMode(GetErrorMode() | SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    WerSetFlags(WER_FAULT_REPORTING_NO_UI);
    SetUnhandledExceptionFilter(ReportFault);
    std::set_terminate([] {
        auto activeException = std::current_exception();
        try {
            if (activeException) std::rethrow_exception(activeException);
        } catch (const std::exception& e) {
            std::fprintf(stderr, "Unhandled C++ exception: %s\n", e.what());
        } catch (...) {
            std::fputs("Unhandled C++ exception\n", stderr);
        }
        if (!activeException) std::fputs("std::terminate\n", stderr);
        std::fflush(stderr);
        std::_Exit(EXIT_FAILURE);
    });
}
}
