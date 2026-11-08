---
title: MDEditor 示例
tags: [markdown, demo]
---

# MDEditor 功能演示

左边编辑，右边**实时渲染**。支持 *斜体*、~~删除线~~、`行内代码` 和 [链接](https://www.apple.com)。

## 列表与任务

- 无序列表项
  - 嵌套一层
- [x] 已完成的任务
- [ ] 待办任务

1. 有序列表
2. 第二项

## 代码高亮

```swift
struct Greeting {
    let name: String
    func say() -> String { "Hello, \(name)!" }
}
```

```python
def fib(n: int) -> int:
    return n if n < 2 else fib(n - 1) + fib(n - 2)
```

```latex
\begin{equation}
  \mathcal{L} = \sum_i \log p(x_i \mid \theta)
\end{equation}
```

## 数学公式

行内公式 $E = mc^2$，价格 $5 和 $10 不会被误认为公式。

$$
\int_{-\infty}^{\infty} e^{-x^2}\,dx = \sqrt{\pi}
$$

## 表格

| 功能 | 状态 | 说明 |
| :--- | :---: | ---: |
| 实时预览 | ✅ | 输入即渲染 |
| 滚动同步 | ✅ | 按源码行对齐 |
| 深色模式 | ✅ | 跟随系统 |

## 引用与提示

> 好的工具应该隐形。

> [!NOTE]
> 这是一条 GitHub 风格的提示块。

> [!WARNING]
> 警告类型也支持。

---

<details>
<summary>点击展开</summary>

折叠内容里的 **Markdown** 也会渲染。

</details>

<script>alert('raw script tags are stripped')</script>

[跳到顶部](#mdeditor-功能演示)
