#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <string>
#include <vector>
#include <map>
#include <deque>
#include <atomic>
#include <algorithm>
#include <cwctype>
#include <process.h>

// Explorer reserves the overlay position (bottom-left). Seven fixed icons avoid
// allocating one of Windows' scarce overlay slots for every configured layer.
static HMODULE module;
static std::atomic<long> objects{0};
static std::atomic<bool> workerStarted{false};
static const COLORREF palette[] = {
#define PM_COLOR(hex) RGB(((hex)>>16)&255,((hex)>>8)&255,(hex)&255),
#include "../PolyMount.Core/Core/OriginPalette.inc"
#undef PM_COLOR
};
static const GUID baseId = {0x7d619ac0,0xdbe2,0x4f33,{0x9a,0x11,0x6c,0xc2,0xc8,0x32,0x90,0x01}};
struct Cached { int slot = -1; ULONGLONG expires = 0; bool pending = false; ULONGLONG seen = 0; };
// Win32 synchronization, not the C++ runtime's. A shell extension is loaded into whatever
// process draws a folder's icons, and that process may carry an older app-local MSVCP140 than
// the one this DLL was built with (Rider ships 14.29 next to rider64.exe while the current
// toolset is 14.51). The STL's mutex/condition_variable keep their state inside the object, so
// the two versions disagree about its layout and _Mtx_lock dereferences a null field -- the
// host process dies with an access violation. SRWLOCK/CONDITION_VARIABLE are OS objects, so
// whichever CRT the host loaded is irrelevant.
static SRWLOCK gate = SRWLOCK_INIT;
static CONDITION_VARIABLE wake = CONDITION_VARIABLE_INIT;
struct GateLock {
    GateLock() { AcquireSRWLockExclusive(&gate); }
    ~GateLock() { ReleaseSRWLockExclusive(&gate); }
    GateLock(const GateLock&) = delete;
    GateLock& operator=(const GateLock&) = delete;
};
struct TryGateLock {
    bool owned = TryAcquireSRWLockExclusive(&gate) != FALSE;
    TryGateLock() = default;
    ~TryGateLock() { if(owned) ReleaseSRWLockExclusive(&gate); }
    TryGateLock(const TryGateLock&) = delete;
    TryGateLock& operator=(const TryGateLock&) = delete;
};
static std::map<std::wstring,Cached> entries;
static std::deque<std::wstring> requests;
static std::vector<std::wstring> roots;
// The mount point each instance's worker owns, and the pipe that worker answers on. The supervisor
// knows the addresses; a worker is the only process that can say what a path resolves to, so a
// probe goes to the one that owns the path.
static std::vector<std::pair<std::wstring,std::wstring>> routes;

static std::wstring Normalize(std::wstring s) {
    std::transform(s.begin(),s.end(),s.begin(),[](wchar_t c){return (wchar_t)towlower(c);});
    while(s.size()>3 && (s.back()==L'\\' || s.back()==L'/')) s.pop_back();
    return s;
}
static std::string Utf8(const std::wstring& s) {
    int n=WideCharToMultiByte(CP_UTF8,0,s.data(),(int)s.size(),nullptr,0,nullptr,nullptr);
    std::string result(n,0); WideCharToMultiByte(CP_UTF8,0,s.data(),(int)s.size(),result.data(),n,nullptr,nullptr); return result;
}
static std::wstring Wide(const std::string& s) {
    int n=MultiByteToWideChar(CP_UTF8,0,s.data(),(int)s.size(),nullptr,0);
    std::wstring result(n,0); MultiByteToWideChar(CP_UTF8,0,s.data(),(int)s.size(),result.data(),n); return result;
}
static std::string Escape(const std::string& s) {
    std::string result;
    for(unsigned char c:s) { if(c=='"'||c=='\\') result+='\\'; if(c<32) return ""; result+=c; } return result;
}
// Extract JSON string fields, decoding the escaped paths emitted by System.Text.Json.
static std::vector<std::string> Fields(const std::string& json,const std::string& key) {
    std::vector<std::string> out; size_t pos=0;
    const std::string token="\""+key+"\"";
    while((pos=json.find(token,pos))!=std::string::npos) {
        pos=json.find(':',pos+token.size()); if(pos==std::string::npos) break;
        pos=json.find_first_not_of(" \t\r\n",pos+1); if(pos==std::string::npos) break;
        if(json[pos]!='"') { ++pos; continue; } ++pos; std::string value;
        while(pos<json.size()) {
            char c=json[pos++]; if(c=='"') break;
            if(c=='\\' && pos<json.size()) {
                c=json[pos++];
                if(c=='u' && pos+4<=json.size()) {
                    unsigned long code=strtoul(json.substr(pos,4).c_str(),nullptr,16); pos+=4;
                    std::wstring decoded(1,(wchar_t)code);
                    if(code>=0xd800 && code<=0xdbff && json.substr(pos,2)=="\\u" && pos+6<=json.size()) {
                        decoded+=(wchar_t)strtoul(json.substr(pos+2,4).c_str(),nullptr,16); pos+=6;
                    }
                    value+=Utf8(decoded); continue;
                }
                if(c=='n') c='\n'; else if(c=='r') c='\r'; else if(c=='t') c='\t';
            }
            value+=c;
        } out.push_back(value);
    } return out;
}
// Everything from the first occurrence of a key onwards. The instance list is the last thing in a
// control response, so slicing it off is enough to read its own fields without picking up the
// mount points of the status that precedes it.
static std::string From(const std::string& json,const std::string& key) {
    size_t pos=json.find("\""+key+"\"");
    return pos==std::string::npos ? std::string() : json.substr(pos);
}
static bool Exchange(const std::string& request,std::string& response,const std::wstring& pipeName=L"PolyMount.Control") {
    std::wstring path=L"\\\\.\\pipe\\"+pipeName;
    // The server listens with one instance at a time: it creates the next one only after the
    // previous connection was accepted, so a client that arrives in that window gets
    // ERROR_FILE_NOT_FOUND (or ERROR_PIPE_BUSY) even though the service is running. The managed
    // control client retries inside NamedPipeClientStream.Connect; this one has to do it itself.
    HANDLE pipe=INVALID_HANDLE_VALUE;
    const ULONGLONG deadline=GetTickCount64()+500;
    for(;;) {
        pipe=CreateFileW(path.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr);
        if(pipe!=INVALID_HANDLE_VALUE) break;
        DWORD error=GetLastError();
        if((error!=ERROR_PIPE_BUSY && error!=ERROR_FILE_NOT_FOUND) || GetTickCount64()>=deadline) return false;
        Sleep(10);
    }
    auto io=[&](bool write,void* data,DWORD length,DWORD& count) {
        OVERLAPPED op{}; op.hEvent=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        BOOL ok=write?WriteFile(pipe,data,length,&count,&op):ReadFile(pipe,data,length,&count,&op);
        if(!ok && GetLastError()==ERROR_IO_PENDING) {
            if(WaitForSingleObject(op.hEvent,2000)==WAIT_OBJECT_0) ok=GetOverlappedResult(pipe,&op,&count,FALSE);
            else { CancelIoEx(pipe,&op); GetOverlappedResult(pipe,&op,&count,TRUE); ok=FALSE; }
        }
        CloseHandle(op.hEvent); return ok!=FALSE;
    };
    std::string message=request+"\n"; DWORD count=0;
    bool ok=io(true,message.data(),(DWORD)message.size(),count) && count==message.size();
    char buffer[4096];
    while(ok && response.size()<1024*1024) {
        ok=io(false,buffer,sizeof(buffer),count); if(!ok || !count) break;
        response.append(buffer,count); if(response.find('\n')!=std::string::npos) break;
    }
    CloseHandle(pipe); return ok && response.find('\n')!=std::string::npos;
}
static int ColorSlot(const std::string& color) {
    if(color.size()!=7 || color[0]!='#') return -1;
    unsigned long rgb=strtoul(color.substr(1).c_str(),nullptr,16);
    int r=(rgb>>16)&255,g=(rgb>>8)&255,b=rgb&255,slot=0,best=INT_MAX;
    for(int i=0;i<7;++i) { int dr=r-GetRValue(palette[i]),dg=g-GetGValue(palette[i]),db=b-GetBValue(palette[i]); int d=dr*dr+dg*dg+db*db; if(d<best){best=d;slot=i;} } return slot;
}
static bool UnderRoot(const std::wstring& path) {
    for(const auto& root:roots) if(path==root || (path.size()>root.size() && path.compare(0,root.size(),root)==0 && path[root.size()]==L'\\')) return true;
    return false;
}
// The pipe of the instance that owns a path: the deepest mount point containing it. Empty when
// nothing is running that instance, and then there is nothing to ask.
static std::wstring PipeFor(const std::wstring& path) {
    std::wstring normalized=Normalize(path),best; size_t length=0;
    GateLock lock;
    for(const auto& route:routes)
        if(route.first.size()>=length && (normalized==route.first ||
            (normalized.size()>route.first.size() && normalized.compare(0,route.first.size(),route.first)==0 && normalized[route.first.size()]==L'\\')))
            { length=route.first.size(); best=route.second; }
    return best;
}
static void Worker() {
    ULONGLONG refresh=0;
    for(;;) {
        if(GetTickCount64()>=refresh) {
            std::string response; std::vector<std::wstring> next;
            if(Exchange("{\"Cmd\":\"status\"}",response)) for(const auto& root:Fields(response,"MountPoint")) next.push_back(Normalize(Wide(root)));
            std::vector<std::pair<std::wstring,std::wstring>> nextRoutes;
            std::string listed;
            if(Exchange("{\"Cmd\":\"instances\"}",listed)) {
                std::string instances=From(listed,"Instances");
                auto places=Fields(instances,"MountPoint"),pipes=Fields(instances,"Pipe");
                for(size_t i=0;i<places.size() && i<pipes.size();++i) nextRoutes.emplace_back(Normalize(Wide(places[i])),Wide(pipes[i]));
            }
            bool changed;
            { GateLock lock; changed=roots!=next; roots=next; routes=nextRoutes;
              // Recheck recently displayed items even when Explorer does not request a new
              // overlay evaluation. Opening a folder can cache its listing without a shell
              // change notification; its icon in the parent window still needs updating.
              const auto now=GetTickCount64();
              for(auto& pair:entries) { auto& e=pair.second;
                if(!e.pending && e.expires<=now && now-e.seen<60000 && requests.size()<128) {
                    e.pending=true;requests.push_back(pair.first);
                }
              }
            }
            refresh=GetTickCount64()+5000;
            // Ask Explorer to re-evaluate icons after initial discovery/config changes.
            if(changed) for(const auto& root:next) SHChangeNotify(SHCNE_UPDATEDIR,SHCNF_PATHW,root.c_str(),nullptr);
        }
        std::wstring path;
        {
            // SleepConditionVariableSRW releases the lock while it waits and takes it back
            // before returning; a timeout (or a spurious wake) simply re-checks the queue.
            AcquireSRWLockExclusive(&gate);
            while(requests.empty()) if(!SleepConditionVariableSRW(&wake,&gate,500,0)) break;
            if(requests.empty()) { ReleaseSRWLockExclusive(&gate); continue; }
            path=requests.front(); requests.pop_front();
            ReleaseSRWLockExclusive(&gate);
        }
        std::string response;
        int slot=-1;
        std::wstring pipe=PipeFor(path);
        if(!pipe.empty() && Exchange("{\"Cmd\":\"probe\",\"Path\":\""+Escape(Utf8(path))+"\"}",response,pipe)) {
            auto colors=Fields(response,"EffectiveColor"); if(!colors.empty()) slot=ColorSlot(colors[0]);
        }
        bool changed;
        { GateLock lock; auto& e=entries[path]; changed=e.slot!=slot; e.slot=slot; e.expires=GetTickCount64()+5000; e.pending=false; }
        if(changed) SHChangeNotify(SHCNE_UPDATEITEM,SHCNF_PATHW,path.c_str(),nullptr);
    }
}
static unsigned __stdcall WorkerStart(void*) { Worker(); return 0; }

class Overlay final : public IShellIconOverlayIdentifier {
    std::atomic<ULONG> refs{1}; int slot;
public:
    explicit Overlay(int s):slot(s){++objects;}
    ~Overlay(){--objects;}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override {
        if(!out) return E_POINTER; *out=nullptr;
        if(iid==IID_IUnknown || iid==IID_IShellIconOverlayIdentifier){*out=static_cast<IShellIconOverlayIdentifier*>(this);AddRef();return S_OK;} return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override{ULONG n=--refs;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE GetOverlayInfo(LPWSTR file,int size,int* index,DWORD* flags) override {
        if(!file||!index||!flags) return E_POINTER;
        wchar_t location[MAX_PATH]; GetModuleFileNameW(module,location,MAX_PATH);
        std::wstring path(location); path=path.substr(0,path.find_last_of(L"\\/"))+L"\\icons\\palette-dot-"+std::to_wstring(slot)+L".ico";
        if(path.size()+1>(size_t)size) return E_INVALIDARG;
        wcscpy_s(file,size,path.c_str());*index=0;*flags=ISIOI_ICONFILE|ISIOI_ICONINDEX;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetPriority(int* priority) override{if(!priority)return E_POINTER;*priority=0;return S_OK;}
    HRESULT STDMETHODCALLTYPE IsMemberOf(LPCWSTR file,DWORD) override {
        try {
        if(!file) return S_FALSE;
        bool expected=false; if(workerStarted.compare_exchange_strong(expected,true)) {
            // _beginthreadex (ucrt) rather than std::thread: the STL's thread object is tied to
            // the MSVCP140 that happens to be loaded, the same hazard the lock above avoids.
            uintptr_t thread=_beginthreadex(nullptr,0,WorkerStart,nullptr,0,nullptr);
            if(thread) CloseHandle(reinterpret_cast<HANDLE>(thread)); else workerStarted=false;
        }
        std::wstring path=Normalize(file);
        // Explorer callbacks must never wait for the worker's cache maintenance.
        TryGateLock lock;
        if(!lock.owned) return S_FALSE;
        if(!UnderRoot(path)) return S_FALSE;
        if(entries.size()>=10000 && entries.find(path)==entries.end()) return S_FALSE;
        auto& e=entries[path];
        e.seen=GetTickCount64();
        if(!e.pending && e.expires<=GetTickCount64() && requests.size()<512) { e.pending=true;requests.push_back(path);WakeConditionVariable(&wake); }
        return e.slot==slot ? S_OK : S_FALSE;
        } catch(...) { return S_FALSE; }
    }
};
class Factory final : public IClassFactory {
    std::atomic<ULONG> refs{1};int slot;
public:
    explicit Factory(int s):slot(s){++objects;} ~Factory(){--objects;}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override {
        if(!out)return E_POINTER;*out=nullptr;if(iid==IID_IUnknown||iid==IID_IClassFactory){*out=this;AddRef();return S_OK;}return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override{return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override{ULONG n=--refs;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer,REFIID iid,void** out) override {
        if(outer)return CLASS_E_NOAGGREGATION;auto* instance=new Overlay(slot);auto result=instance->QueryInterface(iid,out);instance->Release();return result;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {if(lock)++objects;else --objects;return S_OK;}
};
STDAPI DllGetClassObject(REFCLSID clsid,REFIID iid,void** out) {
    GUID test=clsid;int slot=(int)(test.Data1-baseId.Data1);test.Data1=baseId.Data1;
    if(test!=baseId||slot<0||slot>=7)return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory=new Factory(slot);HRESULT result=factory->QueryInterface(iid,out);factory->Release();return result;
}
STDAPI DllCanUnloadNow(){return !objects&&!workerStarted ? S_OK:S_FALSE;}
BOOL WINAPI DllMain(HINSTANCE handle,DWORD reason,LPVOID){if(reason==DLL_PROCESS_ATTACH){module=handle;DisableThreadLibraryCalls(handle);}return TRUE;}
