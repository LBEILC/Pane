#pragma once
#include <atomic>
#include <memory>
#include <vector>
// 单写者、多读者快照。回调只做原子计数，不加锁、不销毁旧快照。
// 写者回收无读者时的退休版本；读者增加计数后才加载指针。
template<class T> class ReadSnapshot {
 std::atomic<unsigned> readers{0};
 std::atomic<const T*> pointer{nullptr};
 std::unique_ptr<T> current;
 std::vector<std::unique_ptr<T>> retired;
public:
 struct View {
  ReadSnapshot& owner;const T* value;
  explicit View(ReadSnapshot& source):owner(source){owner.readers.fetch_add(1);value=owner.pointer.load();}
  ~View(){owner.readers.fetch_sub(1);}
  View(const View&)=delete;View& operator=(const View&)=delete;
  explicit operator bool()const{return value!=nullptr;}
  const T* operator->()const{return value;}
  const T& operator*()const{return *value;}
 };
 View read(){return View(*this);}
 void publish(std::unique_ptr<T> next){
  auto previous=std::move(current);current=std::move(next);pointer.store(current.get());
  if(previous)retired.push_back(std::move(previous));
  if(readers.load()==0)retired.clear();
 }
};
